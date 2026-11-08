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

namespace TrustedInventory.IntegrationTests.Outbound;

/// <summary>Picking and unpick (OUT-003, OUT-004, STK-002, DEC-054) through the API on real PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PickingTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "OUT-003")]
    [Trait("Requirement", "OUT-004")]
    [Trait("Requirement", "STK-002")]
    [Trait("Requirement", "STK-007")]
    [Trait("Requirement", "STK-004")]
    public async Task A_scanned_partial_then_full_pick_moves_stock_and_reservation_to_staging_together()
    {
        var w = await WorldAsync("pick-flow");
        var source = await BucketAsync(w, w.Lotted, w.A01, 10, ("L-1", 90));
        var order = await OrderAsync(w, Line(w.Lotted, "6"));
        await (await AllocateAsync(w, order)).ExpectAsync(HttpStatusCode.OK);

        // OUT-003: one task per reservation with the exact bin, product, lot, expiry and quantity.
        var task = Assert.Single(await ItemsAsync(w.Client, $"/api/v1/picks?orderId={order}"));
        var pick = (string)task["id"]!;
        Assert.Equal(w.A01, (string?)task["sourceBinId"]);
        Assert.Equal("A-01", (string?)task["sourceBinCode"]);
        Assert.Equal("LOT", (string?)task["sku"]);
        Assert.Equal("L-1", (string?)task["lotNumber"]);
        Assert.NotNull((string?)task["expiryDate"]);
        Assert.Equal(["6", "0", "6", "Active"], Strings(task, "quantity", "pickedQuantity", "remainingQuantity", "state"));
        Assert.Null(task["stagingBinId"]);

        // Partial pick: 4 of 6 into staging, on hand and reserved move together (STK-002).
        var key = Guid.NewGuid();
        var body = Scan(w.A01, w.Lotted, "4", w.Staging, "L-1");
        var first = await (await PostAsync(w, $"/api/v1/picks/{pick}/confirm", key, "\"1\"", body)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["4", "2", "2"], Strings(first, "pickedQuantity", "remainingQuantity", "version"));
        var staging = await BucketInAsync(w.Staging, w.Lotted);
        Assert.Equal("6|2", await QtyAsync(source));
        Assert.Equal("4|4", await QtyAsync(staging));
        Assert.Equal("PartPicked", (string?)(await OrderJsonAsync(w, order))["status"]);
        var segments = await SegmentsAsync(order);
        Assert.Equal(["Active:2:" + source, "Picked:4:" + staging], segments);
        Assert.Equal(await ScalarAsync<Guid>("SELECT reservation_id FROM ti.stock_movement WHERE id = @m", ("m", Guid.Parse((string)first["movementId"]!))),
            await ScalarAsync<Guid>("SELECT id FROM ti.reservation WHERE parent_reservation_id IS NOT NULL AND bucket_id = @b", ("b", Guid.Parse(staging))));

        // STK-007: a replay returns the same answer and moves nothing again.
        var replay = await (await PostAsync(w, $"/api/v1/picks/{pick}/confirm", key, "\"1\"", body)).ExpectAsync(HttpStatusCode.OK);
        Assert.True(JsonNode.DeepEquals(first, replay));
        Assert.Equal("6|2", await QtyAsync(source));

        // The rest of a task goes to the same staging bin.
        await (await PostAsync(w, $"/api/v1/picks/{pick}/confirm", Guid.NewGuid(), "\"2\"", Scan(w.A01, w.Lotted, "2", w.Staging2, "L-1")))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        var last = await (await PostAsync(w, $"/api/v1/picks/{pick}/confirm", Guid.NewGuid(), "\"2\"", Scan(w.A01, w.Lotted, "2", w.Staging, "L-1")))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["6", "0"], Strings(last, "pickedQuantity", "remainingQuantity"));
        Assert.Equal("4|0", await QtyAsync(source));
        Assert.Equal("6|6", await QtyAsync(staging));
        Assert.Equal("Picked", (string?)(await OrderJsonAsync(w, order))["status"]);
        Assert.Equal(["Picked:6:" + staging], await SegmentsAsync(order)); // the child folded into the task's own segment

        var done = await (await w.Client.GetAsync($"/api/v1/picks/{pick}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["Picked", w.Staging], Strings(done, "state", "stagingBinId"));
        Assert.NotNull((string?)done["pickedBy"]);
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM ti.pick_confirmation WHERE tenant_id = @t", w.Tenant));
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t AND movement_type = 'Pick'", w.Tenant));
        await ReconcileAsync();
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", w.Tenant));
    }

    [Fact]
    [Trait("Requirement", "OUT-003")]
    [Trait("Requirement", "OUT-004")]
    public async Task Wrong_scans_other_batches_unsuitable_staging_and_stale_versions_change_nothing()
    {
        var w = await WorldAsync("pick-scan");
        var early = await BucketAsync(w, w.Medical, w.C01, 5, ("B-EARLY", 60));
        var late = await BucketAsync(w, w.Medical, w.C02, 5, ("B-LATE", 200));
        var order = await OrderAsync(w, Line(w.Medical, "3"));
        await (await AllocateAsync(w, order)).ExpectAsync(HttpStatusCode.OK);
        var pick = (string)Assert.Single(await ItemsAsync(w.Client, $"/api/v1/picks?orderId={order}"))["id"]!;
        var url = $"/api/v1/picks/{pick}/confirm";

        async Task Refused(object body, string field, string code = "VALIDATION_FAILED", string version = "\"1\"")
        {
            var problem = await (await PostAsync(w, url, Guid.NewGuid(), version, body)).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, code);
            Assert.Contains(problem["errors"]!.AsArray(), e => (string?)e!["field"] == field);
        }
        await Refused(Scan(w.C02, w.Medical, "3", w.ColdStaging, "B-EARLY"), "scannedBinId");
        await Refused(Scan(w.C01, w.General, "3", w.ColdStaging, "B-EARLY"), "scannedProductId");
        await Refused(Scan(w.C01, w.Medical, "3", w.ColdStaging, "B-LATE"), "scannedLotNumber");   // no batch substitution
        await Refused(Scan(w.C01, w.Medical, "3", w.ColdStaging, null), "scannedLotNumber");
        await Refused(Scan(w.C01, w.Medical, "4", w.ColdStaging, "B-EARLY"), "quantity");
        await Refused(Scan(w.C01, w.Medical, "3", w.C02, "B-EARLY"), "stagingBinId");             // a storage bin is not staging
        await Refused(Scan(w.C01, w.Medical, "3", w.Staging, "B-EARLY"), "stagingBinId", "INCOMPATIBLE_BIN"); // ambient staging for cold stock
        await Refused(Scan(w.C01, w.Medical, "3", w.ColdStaging, "B-EARLY", ["SN-1"]), "serialNumbers");
        await (await PostAsync(w, url, Guid.NewGuid(), "\"7\"", Scan(w.C01, w.Medical, "3", w.ColdStaging, "B-EARLY")))
            .ExpectProblemAsync(HttpStatusCode.PreconditionFailed, "VERSION_CONFLICT");
        Assert.Equal("5|3", await QtyAsync(early));
        Assert.Equal("5|0", await QtyAsync(late));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t AND movement_type = 'Pick'", w.Tenant));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.pick WHERE tenant_id = @t AND version = 1", w.Tenant));

        await (await PostAsync(w, url, Guid.NewGuid(), "\"1\"", Scan(w.C01, w.Medical, "3", w.ColdStaging, "B-EARLY"))).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("2|0", await QtyAsync(early));

        // Serial-tracked stock: exactly the reserved unit's serial.
        var unit = await BucketAsync(w, w.Serialized, w.A01, 1, serial: "SN-42");
        var serialOrder = await OrderAsync(w, Line(w.Serialized, "1"));
        await (await AllocateAsync(w, serialOrder)).ExpectAsync(HttpStatusCode.OK);
        var serialPick = (string)Assert.Single(await ItemsAsync(w.Client, $"/api/v1/picks?orderId={serialOrder}"))["id"]!;
        url = $"/api/v1/picks/{serialPick}/confirm";
        Assert.Equal(["SN-42"], (await (await w.Client.GetAsync($"/api/v1/picks/{serialPick}")).ExpectAsync(HttpStatusCode.OK))["serialNumbers"]!
            .AsArray().Select(s => (string)s!).ToArray());
        await Refused(Scan(w.A01, w.Serialized, "1", w.Staging, null), "serialNumbers");
        await Refused(Scan(w.A01, w.Serialized, "1", w.Staging, null, ["SN-41"]), "serialNumbers");
        await (await PostAsync(w, url, Guid.NewGuid(), "\"1\"", Scan(w.A01, w.Serialized, "1", w.Staging, null, ["SN-42"]))).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("0|0", await QtyAsync(unit));
    }

    [Fact]
    [Trait("Requirement", "OUT-004")]
    [Trait("Requirement", "OPS-001")]
    [Trait("Requirement", "STK-008")]
    public async Task Blocked_stock_an_audit_failure_and_an_expired_reservation_refuse_picking()
    {
        var w = await WorldAsync("pick-blocked");
        var bucket = await BucketAsync(w, w.General, w.A01, 10);
        var order = await OrderAsync(w, Line(w.General, "4"));
        await (await AllocateAsync(w, order)).ExpectAsync(HttpStatusCode.OK);
        var pick = (string)Assert.Single(await ItemsAsync(w.Client, $"/api/v1/picks?orderId={order}"))["id"]!;
        var url = $"/api/v1/picks/{pick}/confirm";
        var body = Scan(w.A01, w.General, "4", w.Staging, null);

        var alert = await BlockAsync(w, bucket);
        await (await PostAsync(w, url, Guid.NewGuid(), "\"1\"", body)).ExpectProblemAsync(HttpStatusCode.Conflict, "STOCK_BLOCKED");
        await ExecAsync("UPDATE ti.alert SET status = 'Closed' WHERE id = @a", ("a", alert));

        await ExecAsync("UPDATE ti.reservation SET expires_at = now() - interval '1 minute' WHERE tenant_id = @t", ("t", w.Tenant));
        await (await PostAsync(w, url, Guid.NewGuid(), "\"1\"", body)).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await ExecAsync("UPDATE ti.reservation SET expires_at = now() + interval '1 hour' WHERE tenant_id = @t", ("t", w.Tenant));

        var audit = await ScalarAsync<Guid>("""
            INSERT INTO ti.alert (tenant_id, type, severity, dedupe_key, details, created_by) VALUES (@t, 'AuditChainFailure', 'High', 'audit-chain', '{}', 'test')
            RETURNING id
            """, ("t", w.Tenant));
        await (await PostAsync(w, url, Guid.NewGuid(), "\"1\"", body)).ExpectProblemAsync(HttpStatusCode.Conflict, "STOCK_BLOCKED");
        await ExecAsync("UPDATE ti.alert SET status = 'Closed' WHERE id = @a", ("a", audit));
        Assert.Equal("10|4", await QtyAsync(bucket));

        await (await PostAsync(w, url, Guid.NewGuid(), "\"1\"", body)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("6|0", await QtyAsync(bucket));
    }

    [Fact]
    [Trait("Requirement", "OUT-006")]
    [Trait("Requirement", "STK-002")]
    [Trait("Requirement", "STK-007")]
    [Trait("Requirement", "STK-004")]
    public async Task Unpick_returns_stock_and_reservation_and_releasing_a_part_picked_task_keeps_the_picked_part()
    {
        var w = await WorldAsync("pick-unpick");
        var a01 = await BucketAsync(w, w.General, w.A01, 10);
        var order = await OrderAsync(w, Line(w.General, "6"));
        await (await AllocateAsync(w, order)).ExpectAsync(HttpStatusCode.OK);
        var pick = (string)Assert.Single(await ItemsAsync(w.Client, $"/api/v1/picks?orderId={order}"))["id"]!;
        await (await PostAsync(w, $"/api/v1/picks/{pick}/confirm", Guid.NewGuid(), "\"1\"", Scan(w.A01, w.General, "6", w.Staging, null)))
            .ExpectAsync(HttpStatusCode.OK);
        var staging = await BucketInAsync(w.Staging, w.General);
        var unpickUrl = $"/api/v1/picks/{pick}/unpick";

        // Unpick 2 to another storage bin: the task's own segment is Active there, the rest stays staged.
        await (await PostAsync(w, unpickUrl, Guid.NewGuid(), "\"2\"", new { quantity = "2", targetBinId = w.A02 }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"); // a reason is required
        var key = Guid.NewGuid();
        var body = new { quantity = "2", targetBinId = w.A02, reason = "Damaged carton found at packing" };
        var returned = await (await PostAsync(w, unpickUrl, key, "\"2\"", body)).ExpectAsync(HttpStatusCode.OK);
        var a02 = await BucketInAsync(w.A02, w.General);
        Assert.Equal("4|0", await QtyAsync(a01));
        Assert.Equal("4|4", await QtyAsync(staging));
        Assert.Equal("2|2", await QtyAsync(a02));
        Assert.Equal(["Active:2:" + a02, "Picked:4:" + staging], await SegmentsAsync(order));
        var task = await (await w.Client.GetAsync($"/api/v1/picks/{pick}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["6", "4", "2", "Active", w.A02], Strings(task, "quantity", "pickedQuantity", "remainingQuantity", "state", "sourceBinId"));
        Assert.Equal("PartPicked", (string?)(await OrderJsonAsync(w, order))["status"]);
        Assert.Equal(["Unpick", "Damaged carton found at packing"], new[]
        {
            await ScalarAsync<string>("SELECT movement_type::text FROM ti.stock_movement WHERE id = @m", ("m", Guid.Parse((string)returned["movementId"]!))),
            await ScalarAsync<string>("SELECT reason FROM ti.stock_movement WHERE id = @m", ("m", Guid.Parse((string)returned["movementId"]!))),
        });
        var replay = await (await PostAsync(w, unpickUrl, key, "\"2\"", body)).ExpectAsync(HttpStatusCode.OK);
        Assert.True(JsonNode.DeepEquals(returned, replay));
        Assert.Equal("2|2", await QtyAsync(a02));

        // While part picked, returned stock must join the rest of the reservation.
        await (await PostAsync(w, unpickUrl, Guid.NewGuid(), "\"3\"", new { quantity = "1", targetBinId = w.A03, reason = "Wrong bin" }))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        await (await PostAsync(w, unpickUrl, Guid.NewGuid(), "\"3\"", new { quantity = "1", targetBinId = w.A02, reason = "Short-dated carton" }))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("3|3", await QtyAsync(staging));
        Assert.Equal("3|3", await QtyAsync(a02));

        // Deallocation releases only the unpicked part; the picked part stays with the task, which completes.
        var orderVersion = (await OrderJsonAsync(w, order))["version"]!.ToString();
        await (await PostAsync(w, $"/api/v1/orders/{order}/deallocate", Guid.NewGuid(), $"\"{orderVersion}\"", new { reason = "Customer reduced the order" }))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("3|0", await QtyAsync(a02));
        Assert.Equal("3|3", await QtyAsync(staging));
        Assert.Equal(["Picked:3:" + staging], await SegmentsAsync(order));
        task = await (await w.Client.GetAsync($"/api/v1/picks/{pick}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["3", "3", "0", "Picked"], Strings(task, "quantity", "pickedQuantity", "remainingQuantity", "state"));

        // Picked stock blocks cancellation (OUT-006) until it is unpicked.
        var cancelVersion = (await OrderJsonAsync(w, order))["version"]!.ToString();
        await (await PostAsync(w, $"/api/v1/orders/{order}/cancel", Guid.NewGuid(), $"\"{cancelVersion}\"", new { reason = "Customer cancelled" }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await PostAsync(w, unpickUrl, Guid.NewGuid(), $"\"{task["version"]}\"", new { quantity = "3", targetBinId = w.A01, reason = "Order cancelled" }))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["Active:3:" + a01], await SegmentsAsync(order));
        cancelVersion = (await OrderJsonAsync(w, order))["version"]!.ToString();
        await (await PostAsync(w, $"/api/v1/orders/{order}/cancel", Guid.NewGuid(), $"\"{cancelVersion}\"", new { reason = "Customer cancelled" }))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Cancelled", await ScalarAsync<string>("SELECT status::text FROM ti.pick WHERE id = @p", ("p", Guid.Parse(pick))));

        // Stock is conserved: 10 units, nothing reserved, and the ledger agrees.
        Assert.Equal(["7|0", "0|0", "3|0"], new[] { await QtyAsync(a01), await QtyAsync(staging), await QtyAsync(a02) });
        await ReconcileAsync();
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", w.Tenant));
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, HttpClient Client, string Warehouse, string A01, string A02, string A03, string Staging, string Staging2,
        string ColdStaging, string C01, string C02, string General, string Lotted, string Medical, string Serialized, string Customer);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "WarehouseManager");
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "Picker");
        async Task<string> Create(string path, object body) => (string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!;
        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" });
        var ambient = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "AMB", name = "Ambient" });
        var cold = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "COLD", name = "Cold", tempMinC = 2, tempMaxC = 8 });
        async Task<string> Bin(string zone, string code, string kind = "Storage") => await Create("/api/v1/bins", new { zoneId = zone, code, kind });
        var a01 = await Bin(ambient, "A-01");
        var a02 = await Bin(ambient, "A-02");
        var a03 = await Bin(ambient, "A-03");
        var staging = await Bin(ambient, "STG", "Staging");
        var staging2 = await Bin(ambient, "STG-2", "Staging");
        var coldStaging = await Bin(cold, "C-STG", "Staging");
        var c01 = await Bin(cold, "C-01");
        var c02 = await Bin(cold, "C-02");
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var profile = await Create("/api/v1/storage-profiles", new
        {
            name = "Cold chain", minC = 2, maxC = 8, monitoringRequired = true,
            excursionPolicy = new { immediateHold = true, notes = "QA reviewed" }, reason = "picking test",
        });
        object Product(string sku, string handling, string tracking, string rotation, string? storage = null) => new
        {
            sku, name = sku, handlingProfile = handling, trackingMode = tracking, baseUomId = unit, basePrecision = 0, rotationRule = rotation, storageProfileId = storage,
        };
        var general = await Create("/api/v1/products", Product("GEN", "General", "Quantity", "FIFO"));
        var lotted = await Create("/api/v1/products", Product("LOT", "General", "Lot", "FEFO"));
        var medical = await Create("/api/v1/products", Product("MED", "Medical", "Lot", "FEFO", profile));
        var serialized = await Create("/api/v1/products", Product("SER", "General", "Serial", "FIFO"));
        var customer = await Create("/api/v1/partners", new { code = "CUS", name = "Customer", type = "Customer" });
        return new World(tenant.TenantId, admin, warehouse, a01, a02, a03, staging, staging2, coldStaging, c01, c02, general, lotted, medical, serialized,
            customer);
    }

    private static object Line(string product, string quantity) => new { productId = product, quantity };

    private static object Scan(string bin, string product, string quantity, string stagingBin, string? lot, string[]? serials = null) => new
    {
        quantity, scannedBinId = bin, scannedProductId = product, scannedLotNumber = lot, serialNumbers = serials, stagingBinId = stagingBin,
    };

    private static string[] Strings(JsonObject o, params string[] names) =>
        names.Select(n => o[n] is JsonValue v ? v.ToString() : o[n]?.ToJsonString() ?? "null").ToArray();

    /// <summary>A confirmed order dispatching tomorrow.</summary>
    private static async Task<string> OrderAsync(World w, params object[] lines)
    {
        var created = await (await w.Client.PostJsonAsync("/api/v1/orders", new
        {
            customerId = w.Customer, warehouseId = w.Warehouse, reference = $"SO-{Guid.NewGuid():N}"[..20],
            plannedDispatchDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), lines,
        })).ExpectAsync(HttpStatusCode.Created);
        await (await w.Client.PostEmptyAsync($"/api/v1/orders/{created["id"]}/confirm", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        return (string)created["id"]!;
    }

    private static async Task<JsonObject> OrderJsonAsync(World w, string order) =>
        await (await w.Client.GetAsync($"/api/v1/orders/{order}")).ExpectAsync(HttpStatusCode.OK);

    private static Task<HttpResponseMessage> AllocateAsync(World w, string order) =>
        PostAsync(w, $"/api/v1/orders/{order}/allocate", Guid.NewGuid(), "\"2\"", new { allowPartial = false });

    private static Task<HttpResponseMessage> PostAsync(World w, string url, Guid key, string ifMatch, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return w.Client.SendAsync(request);
    }

    /// <summary>A stock bucket written as the superuser with a balancing Receipt movement; optional lot or serial.</summary>
    private async Task<string> BucketAsync(World w, string product, string bin, decimal onHand, (string Number, int DaysToExpiry)? lot = null,
        string? serial = null)
    {
        object lotId = DBNull.Value;
        object serialId = DBNull.Value;
        if (lot is { } l)
            lotId = await ScalarAsync<Guid>("""
                INSERT INTO ti.lot (tenant_id, product_id, handling_profile, lot_number, expiry_date, created_by)
                SELECT @t, id, handling_profile, @n, current_date + @d, 'test' FROM ti.product WHERE id = @p RETURNING id
                """, ("t", w.Tenant), ("p", Guid.Parse(product)), ("n", l.Number), ("d", l.DaysToExpiry));
        if (serial is not null)
            serialId = await ScalarAsync<Guid>("INSERT INTO ti.serial (tenant_id, product_id, serial_number, created_by) VALUES (@t, @p, @n, 'test') RETURNING id",
                ("t", w.Tenant), ("p", Guid.Parse(product)), ("n", serial));
        var bucket = await ScalarAsync<Guid>("""
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, serial_id, quality_status, received_at, on_hand_qty, created_by)
            VALUES (@t, @w, @b, @p, @l, @s, 'Available', now() - interval '5 days', @qty, 'test') RETURNING id
            """, ("t", w.Tenant), ("w", Guid.Parse(w.Warehouse)), ("b", Guid.Parse(bin)), ("p", Guid.Parse(product)), ("l", lotId), ("s", serialId),
            ("qty", onHand));
        await ExecAsync("""
            INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, lot_id, serial_id, destination_bucket_id, base_qty,
                                           document_type, document_id, actor_subject, occurred_at)
            VALUES (@t, 'Receipt', 'test-receipt:' || gen_random_uuid(), @p, @l, @s, @b, @q, 'Receipt', gen_random_uuid(), 'test', now())
            """, ("t", w.Tenant), ("p", Guid.Parse(product)), ("l", lotId), ("s", serialId), ("b", bucket), ("q", onHand));
        return bucket.ToString();
    }

    private async Task<Guid> BlockAsync(World w, string bucket)
    {
        var alert = await ScalarAsync<Guid>("""
            INSERT INTO ti.alert (tenant_id, type, severity, dedupe_key, details, created_by)
            VALUES (@t, 'ReconciliationMismatch', 'High', 'ledger-reconciliation', '{}', 'test') RETURNING id
            """, ("t", w.Tenant));
        await ExecAsync("INSERT INTO ti.stock_bucket_block (tenant_id, bucket_id, alert_id, expected_qty, stored_qty, created_by) VALUES (@t, @b, @a, 1, 50, 'test')",
            ("t", w.Tenant), ("b", Guid.Parse(bucket)), ("a", alert));
        return alert;
    }

    private async Task<string> BucketInAsync(string bin, string product) => (await ScalarAsync<Guid>(
        "SELECT id FROM ti.stock_bucket WHERE bin_id = @b AND product_id = @p", ("b", Guid.Parse(bin)), ("p", Guid.Parse(product)))).ToString();

    /// <summary>The order's live reservation segments as "state:qty:bucket", ordered by state.</summary>
    private async Task<string[]> SegmentsAsync(string order) => (await ScalarAsync<string>("""
        SELECT coalesce(string_agg(r.state::text || ':' || trim_scale(r.qty)::text || ':' || r.bucket_id::text, ',' ORDER BY r.state, r.id), '')
        FROM ti.reservation r JOIN ti.outbound_order_line l ON l.id = r.order_line_id
        WHERE l.order_id = @o AND r.state IN ('Active','Picked')
        """, ("o", Guid.Parse(order)))).Split(',', StringSplitOptions.RemoveEmptyEntries);

    private async Task<string> QtyAsync(string bucket) => await ScalarAsync<string>(
        "SELECT trim_scale(on_hand_qty)::text || '|' || trim_scale(reserved_qty)::text FROM ti.stock_bucket WHERE id = @b", ("b", Guid.Parse(bucket)));

    private static async Task<List<JsonObject>> ItemsAsync(HttpClient client, string url) =>
        (await (await client.GetAsync(url)).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray().Select(i => i!.AsObject()).ToList();

    private async Task ReconcileAsync()
    {
        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var runner = new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), new FakeTimeProvider(DateTimeOffset.UtcNow));
        await new LedgerReconciliationJob(runner, fx.Factory.Services.GetRequiredService<IAuditSupport>(), NullLogger<LedgerReconciliationJob>.Instance)
            .RunOnceAsync(CancellationToken.None);
    }

    private async Task<long> CountAsync(string sql, Guid tenant) => await ScalarAsync<long>(sql, ("t", tenant));

    private Task<int> ExecAsync(string sql, params (string Name, object Value)[] args) => TenantProvisioning.ExecAsSuperuserAsync(fx, sql, args);

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args) =>
        (T)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, args))!;
}

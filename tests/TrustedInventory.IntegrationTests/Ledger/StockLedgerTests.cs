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

namespace TrustedInventory.IntegrationTests.Ledger;

/// <summary>
/// Stock projection, eligibility and ledger reads (STK-001, STK-004), and reconciliation with OPS-001 blocking (DEC-049).
/// Putaway arrives in slice 5; until then a consistent putaway (bucket, Putaway movement, staging decrement) is
/// written as the superuser so the ledger stays balanced.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class StockLedgerTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "STK-001")]
    public async Task Only_released_put_away_compatible_unexpired_stock_is_allocatable()
    {
        var w = await WorldAsync("stk-eligible");
        var receipt = await ReceiveAsync(w, Guid.NewGuid(), Item(w.GenLine, w.General, "10", w.AmbientStaging));
        var staging = Guid.Parse((string)receipt["lines"]![0]!["bucketId"]!);

        var received = await BucketAsync(w, staging);
        Assert.Equal(["AwaitingPutaway"], Reasons(received));
        Assert.Equal(["10", "0", "0"], Quantities(received));
        Assert.False((bool)received["eligible"]!);
        Assert.NotNull(received["lastMovementAt"]);

        var storage = await PutAwayAsync(w, staging, w.AmbientStorage, 6m);
        var putAway = await BucketAsync(w, storage);
        Assert.True((bool)putAway["eligible"]!);
        Assert.Empty(Reasons(putAway));
        Assert.Equal(["6", "0", "6"], Quantities(putAway));
        Assert.Equal(["4", "0", "0"], Quantities(await BucketAsync(w, staging)));

        await ExecAsync("UPDATE ti.stock_bucket SET reserved_qty = 2 WHERE id = @b", ("b", storage));
        Assert.Equal(["6", "2", "4"], Quantities(await BucketAsync(w, storage)));
        await ExecAsync("UPDATE ti.bin SET active = false WHERE id = @b", ("b", Guid.Parse(w.AmbientStorage)));
        Assert.Equal(["BinInactive"], Reasons(await BucketAsync(w, storage)));
        await ExecAsync("UPDATE ti.bin SET active = true WHERE id = @b", ("b", Guid.Parse(w.AmbientStorage)));

        // Expired on arrival (accepted with a reason into Hold): every failing check is named.
        var expired = await ReceiveAsync(w, Guid.NewGuid(), Item(w.LotLine, w.Lotted, "3", w.AmbientStaging, lotNumber: "OLD", expirationDate: "2020-01-01",
            exceptionReason: "Expired on arrival"));
        Assert.Equal(["Hold", "Expired", "AwaitingPutaway"], Reasons(await BucketAsync(w, Guid.Parse((string)expired["lines"]![0]!["bucketId"]!))));

        // Cold-chain stock in an ambient storage bin is incompatible (DEC-012), besides still being quarantined.
        var cold = await ReceiveAsync(w, Guid.NewGuid(), Item(w.MedLine, w.Medical, "5", w.ColdStaging, lotNumber: "M1", expirationDate: "2030-01-01",
            supplierReference: "SR", temperature: 5m));
        var misplaced = await PutAwayAsync(w, Guid.Parse((string)cold["lines"]![0]!["bucketId"]!), w.AmbientStorage, 5m);
        Assert.Equal(["Quarantine", "StorageIncompatible"], Reasons(await BucketAsync(w, misplaced)));

        // Lists hide empty buckets by default; eligibleOnly keeps only allocatable stock.
        var all = await ItemsAsync(w.Client, "/api/v1/stock");
        Assert.DoesNotContain(all, b => (string?)b["onHand"] == "0");
        Assert.Contains(await ItemsAsync(w.Client, "/api/v1/stock?includeEmpty=true"), b => (string?)b["onHand"] == "0"); // the emptied cold staging bucket
        Assert.Equal([storage.ToString()], (await ItemsAsync(w.Client, "/api/v1/stock?eligibleOnly=true")).Select(b => (string)b["id"]!));
    }

    [Fact]
    [Trait("Requirement", "STK-004")]
    [Trait("Requirement", "IAM-001")]
    public async Task The_ledger_is_readable_by_document_and_both_reads_are_warehouse_scoped()
    {
        var w = await WorldAsync("stk-moves");
        var receipt = await ReceiveAsync(w, Guid.NewGuid(), Item(w.GenLine, w.General, "4", w.AmbientStaging), Item(w.SerLine, w.Serialized, "2", w.AmbientStaging,
            serials: ["A1", "A2"]));
        var receiptId = (string)receipt["receiptId"]!;

        var movements = await ItemsAsync(w.Client, $"/api/v1/stock/movements?documentType=Receipt&documentId={receiptId}");
        Assert.Equal(3, movements.Count); // one per receipt line; serial items post one line per unit
        Assert.All(movements, m =>
        {
            Assert.Equal("Receipt", (string?)m["type"]);
            Assert.Null(m["source"]);
            Assert.Equal(w.Warehouse, (string?)m["destination"]!["warehouseId"]);
            Assert.Equal(w.AmbientStaging, (string?)m["destination"]!["binId"]);
            Assert.Equal((string?)m["baseQuantity"], (string?)m["unreversedQuantity"]);
        });
        Assert.Equal(["1", "1", "4"], movements.Select(m => (string)m["baseQuantity"]!).Order());
        var one = await (await w.Client.GetAsync($"/api/v1/stock/movements/{movements[0]["id"]}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal((string?)movements[0]["id"], (string?)one["id"]);

        // A manager scoped to another warehouse sees none of it.
        var other = (string)(await (await w.Client.PostJsonAsync("/api/v1/warehouses", new { code = "W2", name = "Other", timeZone = "UTC" }))
            .ExpectAsync(HttpStatusCode.Created))["id"]!;
        var scoped = await MemberAsync(w, "WarehouseManager", other);
        Assert.Empty(await ItemsAsync(scoped, "/api/v1/stock"));
        Assert.Empty(await ItemsAsync(scoped, "/api/v1/stock/movements"));
        await (await scoped.GetAsync($"/api/v1/stock/{receipt["lines"]![0]!["bucketId"]}")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await scoped.GetAsync($"/api/v1/stock/movements/{movements[0]["id"]}")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    [Trait("Requirement", "STK-004")]
    [Trait("Requirement", "OPS-001")]
    public async Task Reconciliation_raises_one_high_alert_and_blocks_mismatched_buckets_without_repairing_them()
    {
        var w = await WorldAsync("stk-recon");
        var receipt = await ReceiveAsync(w, Guid.NewGuid(), Item(w.GenLine, w.General, "10", w.AmbientStaging));
        var staging = Guid.Parse((string)receipt["lines"]![0]!["bucketId"]!);
        var storage = await PutAwayAsync(w, staging, w.AmbientStorage, 6m);

        await ReconcileAsync();
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", w.Tenant)); // balanced

        // An unexplained direct balance change (simulating corruption) is detected, alerted, blocked, and left as evidence.
        await ExecAsync("UPDATE ti.stock_bucket SET on_hand_qty = on_hand_qty + 1 WHERE id = @b", ("b", storage));
        await ReconcileAsync();
        await ReconcileAsync(); // a second run refreshes the same alert
        Assert.Equal("1|High|Open", await ScalarAsync<string>("""
            SELECT count(*) || '|' || min(severity::text) || '|' || min(status::text) FROM ti.alert
            WHERE tenant_id = @t AND type = 'ReconciliationMismatch'
            """, ("t", w.Tenant)));
        var details = JsonNode.Parse(await ScalarAsync<string>(
            "SELECT details::text FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", ("t", w.Tenant)))!;
        var mismatch = Assert.Single(details["buckets"]!.AsArray())!;
        Assert.Equal(storage.ToString(), (string?)mismatch["bucketId"]);
        Assert.Equal(6m, decimal.Parse((string)mismatch["expectedOnHand"]!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(7m, decimal.Parse((string)mismatch["storedOnHand"]!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.stock_bucket_block WHERE tenant_id = @t", w.Tenant));
        Assert.Equal(["7", "0", "0"], Quantities(await BucketAsync(w, storage))); // never repaired by the job
        Assert.Equal(["Blocked"], Reasons(await BucketAsync(w, storage)));
        Assert.DoesNotContain("Blocked", Reasons(await BucketAsync(w, staging))); // only the mismatched bucket
    }

    [Fact]
    [Trait("Requirement", "OPS-001")]
    [Trait("Requirement", "STK-007")]
    public async Task Blocked_stock_refuses_changes_until_the_alert_is_closed_and_an_audit_failure_blocks_everything()
    {
        var w = await WorldAsync("stk-block");
        var first = await ReceiveAsync(w, Guid.NewGuid(), Item(w.GenLine, w.General, "5", w.AmbientStaging));
        var staging = Guid.Parse((string)first["lines"]![0]!["bucketId"]!);
        await ExecAsync("UPDATE ti.stock_bucket SET on_hand_qty = on_hand_qty + 1 WHERE id = @b", ("b", staging));
        await ReconcileAsync();
        var movementsBefore = await CountAsync("SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t", w.Tenant);

        // Adding to the blocked bucket is refused whole; other buckets still take stock.
        var key = Guid.NewGuid();
        await (await PostAsync(w, key, Receive(w, Item(w.GenLine, w.General, "2", w.AmbientStaging)))).ExpectProblemAsync(HttpStatusCode.Conflict, "STOCK_BLOCKED");
        Assert.Equal(movementsBefore, await CountAsync("SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t", w.Tenant));
        await ReceiveAsync(w, Guid.NewGuid(), Item(w.GenLine, w.General, "1", w.SecondStaging));

        // Closing the alert (the operator procedure) lifts the block; the same key now posts, because a block is not a stored answer.
        await ExecAsync("UPDATE ti.alert SET status = 'Closed', closed_at = now() WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", ("t", w.Tenant));
        await (await PostAsync(w, key, Receive(w, Item(w.GenLine, w.General, "2", w.AmbientStaging)))).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.stock_bucket_block WHERE tenant_id = @t", w.Tenant)); // evidence kept

        // An open audit-chain failure blocks every stock change of the tenant and marks all stock blocked.
        await ExecAsync("""
            INSERT INTO ti.alert (tenant_id, type, severity, dedupe_key, details, created_by)
            VALUES (@t, 'AuditChainFailure', 'High', 'audit-chain', '{}', 'test')
            """, ("t", w.Tenant));
        await (await PostAsync(w, Guid.NewGuid(), Receive(w, Item(w.GenLine, w.General, "1", w.SecondStaging)))).ExpectProblemAsync(HttpStatusCode.Conflict, "STOCK_BLOCKED");
        Assert.All(await ItemsAsync(w.Client, "/api/v1/stock"), b => Assert.Contains("Blocked", Reasons(b)));
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, HttpClient Client, string Warehouse, string AmbientStaging, string SecondStaging, string AmbientStorage,
        string ColdStaging, string General, string Lotted, string Medical, string Serialized, string Each, string Order, string GenLine, string LotLine,
        string MedLine, string SerLine);

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
        var staging = await Create("/api/v1/bins", new { zoneId = ambient, code = "AMB-STG", kind = "Staging" });
        var staging2 = await Create("/api/v1/bins", new { zoneId = ambient, code = "AMB-STG2", kind = "Staging" });
        var storage = await Create("/api/v1/bins", new { zoneId = ambient, code = "AMB-01", kind = "Storage" });
        var coldStaging = await Create("/api/v1/bins", new { zoneId = cold, code = "COLD-STG", kind = "Staging" });
        var each = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        _each = each; // every test item is in the base unit
        var profile = await Create("/api/v1/storage-profiles", new
        {
            name = "Cold chain", minC = 2, maxC = 8, monitoringRequired = true,
            excursionPolicy = new { immediateHold = true, notes = "QA reviewed" }, reason = "stock ledger test",
        });
        object Product(string sku, string handling, string tracking, string? storageProfile = null) =>
            new { sku, name = sku, handlingProfile = handling, trackingMode = tracking, baseUomId = each, basePrecision = 0, storageProfileId = storageProfile };
        var general = await Create("/api/v1/products", Product("GEN", "General", "Quantity"));
        var lotted = await Create("/api/v1/products", Product("LOT", "General", "Lot"));
        var medical = await Create("/api/v1/products", Product("MED", "Medical", "Lot", profile));
        var serialized = await Create("/api/v1/products", Product("SER", "General", "Serial"));
        var supplier = await Create("/api/v1/partners", new { code = "SUP", name = "Supplier", type = "Supplier" });
        var order = await (await admin.PostJsonAsync("/api/v1/purchase-orders", new
        {
            supplierId = supplier, warehouseId = warehouse, reference = "PO-S1",
            lines = new object[]
            {
                new { productId = general, quantity = "1000", uomId = each },
                new { productId = lotted, quantity = "1000", uomId = each },
                new { productId = medical, quantity = "1000", uomId = each },
                new { productId = serialized, quantity = "100", uomId = each },
            },
        })).ExpectAsync(HttpStatusCode.Created);
        var orderId = (string)order["id"]!;
        await (await admin.PostEmptyAsync($"/api/v1/purchase-orders/{orderId}/approve", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        var lines = order["lines"]!.AsArray().Select(l => (string)l!["id"]!).ToList();
        return new World(tenant.TenantId, admin, warehouse, staging, staging2, storage, coldStaging, general, lotted, medical, serialized, each, orderId,
            lines[0], lines[1], lines[2], lines[3]);
    }

    private async Task<HttpClient> MemberAsync(World w, string role, string warehouse)
    {
        var subject = $"user-scoped-{Guid.NewGuid():N}";
        var membership = (string)(await (await w.Client.PostJsonAsync("/api/v1/memberships", new { userSubject = subject })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await w.Client.PostJsonAsync($"/api/v1/memberships/{membership}/role-grants", new { role, warehouseId = warehouse, reason = "stock test" }))
            .ExpectAsync(HttpStatusCode.Created);
        return fx.Client(subject, w.Tenant);
    }

    private sealed record ReceiveBody(string PurchaseOrderLineId, string ProductId, string Quantity, string UomId, string TargetBinId, string? LotNumber,
        string? ExpirationDate, string? SupplierReference, decimal? InitialTemperatureCelsius, string[]? SerialNumbers, string? ExceptionReason);

    private ReceiveBody Item(string line, string product, string quantity, string bin, string? lotNumber = null, string? expirationDate = null,
        string? supplierReference = null, decimal? temperature = null, string[]? serials = null, string? exceptionReason = null) =>
        new(line, product, quantity, _each!, bin, lotNumber, expirationDate, supplierReference, temperature, serials, exceptionReason);

    private string? _each;

    private static object Receive(World w, params ReceiveBody[] items) => new { warehouseId = w.Warehouse, purchaseOrderId = w.Order, items };

    private static async Task<JsonObject> ReceiveAsync(World w, Guid key, params ReceiveBody[] items) =>
        await (await PostAsync(w, key, Receive(w, items))).ExpectAsync(HttpStatusCode.OK);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static Task<HttpResponseMessage> PostAsync(World w, Guid key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/inventory/receive")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Options), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return w.Client.SendAsync(request);
    }

    /// <summary>A balanced putaway until slice 5: new storage bucket, a Putaway movement, and the staging decrement.</summary>
    private async Task<Guid> PutAwayAsync(World w, Guid fromBucket, string toBin, decimal qty)
    {
        var target = await ScalarAsync<Guid>("""
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, serial_id, quality_status, received_at, on_hand_qty, created_by)
            SELECT tenant_id, warehouse_id, @bin, product_id, lot_id, serial_id, quality_status, received_at, @q, 'test' FROM ti.stock_bucket WHERE id = @from
            RETURNING id
            """, ("bin", Guid.Parse(toBin)), ("q", qty), ("from", fromBucket));
        await ExecAsync("""
            INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, lot_id, source_bucket_id, destination_bucket_id,
                                           base_qty, document_type, document_id, actor_subject, occurred_at)
            SELECT tenant_id, 'Putaway', 'test-putaway:' || gen_random_uuid(), product_id, lot_id, id, @to, @q, 'PutawayTask', gen_random_uuid(), 'test', now()
            FROM ti.stock_bucket WHERE id = @from
            """, ("to", target), ("q", qty), ("from", fromBucket));
        await ExecAsync("UPDATE ti.stock_bucket SET on_hand_qty = on_hand_qty - @q WHERE id = @from", ("q", qty), ("from", fromBucket));
        return target;
    }

    private async Task ReconcileAsync()
    {
        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var runner = new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), new FakeTimeProvider(DateTimeOffset.UtcNow));
        await new LedgerReconciliationJob(runner, fx.Factory.Services.GetRequiredService<IAuditSupport>(), NullLogger<LedgerReconciliationJob>.Instance)
            .RunOnceAsync(CancellationToken.None);
    }

    private static async Task<JsonObject> BucketAsync(World w, Guid id) =>
        await (await w.Client.GetAsync($"/api/v1/stock/{id}")).ExpectAsync(HttpStatusCode.OK);

    private static async Task<List<JsonObject>> ItemsAsync(HttpClient client, string url) =>
        (await (await client.GetAsync(url)).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray().Select(i => i!.AsObject()).ToList();

    private static string[] Reasons(JsonObject bucket) => bucket["ineligibilityReasons"]!.AsArray().Select(r => (string)r!).ToArray();

    private static string[] Quantities(JsonObject bucket) => [(string)bucket["onHand"]!, (string)bucket["reserved"]!, (string)bucket["allocatable"]!];

    private async Task<long> CountAsync(string sql, Guid tenant) => await ScalarAsync<long>(sql, ("t", tenant));

    private Task<int> ExecAsync(string sql, params (string Name, object Value)[] args) => TenantProvisioning.ExecAsSuperuserAsync(fx, sql, args);

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args) =>
        (T)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, args))!;
}

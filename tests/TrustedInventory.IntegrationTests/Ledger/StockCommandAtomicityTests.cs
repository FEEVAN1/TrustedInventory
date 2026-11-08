using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Ledger;

/// <summary>
/// ARC-003: a stock command writes stock, documents, audit, idempotency record and outbox in one transaction, so a fault
/// at any point rolls all of it back. Faults are real database errors: a temporary trigger, added as the superuser for
/// this tenant only, raises on insert into one chosen table. Afterwards a retry with the same key posts exactly once.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class StockCommandAtomicityTests(ApiFixture fx)
{
    /// <summary>Tables a receipt writes, from early in the command (lots, serials, buckets) to the end (outbox, audit, idempotency).</summary>
    public static TheoryData<string> ReceiptWrites() => new()
    {
        "lot", "serial", "stock_bucket", "receipt", "receipt_line", "receipt_line_serial", "stock_movement", "warehouse_task",
        "document_operation", "outbox_event", "audit_event", "idempotency_record",
    };

    [Theory]
    [MemberData(nameof(ReceiptWrites))]
    [Trait("Requirement", "ARC-003")]
    [Trait("Requirement", "STK-007")]
    public async Task A_fault_anywhere_in_a_receipt_rolls_back_every_write(string faultTable)
    {
        var w = await WorldAsync($"atom-r-{faultTable}");
        var before = await FootprintAsync(w);
        var key = Guid.NewGuid();
        var body = Receipt(w);

        await using (await FaultAsync(w, faultTable))
        {
            var response = await PostAsync(w, "/api/v1/inventory/receive", key, body);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        Assert.Equal(before, await FootprintAsync(w)); // nothing of the command survived, not even the idempotency record
        await AssertNoFaultLeftAsync();

        // The failure was not stored as the answer: the same key now posts, once.
        await (await PostAsync(w, "/api/v1/inventory/receive", key, body)).ExpectAsync(HttpStatusCode.OK);
        await (await PostAsync(w, "/api/v1/inventory/receive", key, body)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("receipts=1 movements=4 onHand=10", await PostedAsync(w));
    }

    [Theory]
    [InlineData("stock_movement")]
    [InlineData("document_operation")]
    [InlineData("audit_event")]
    [InlineData("idempotency_record")]
    [Trait("Requirement", "ARC-003")]
    public async Task A_fault_in_a_putaway_leaves_task_buckets_and_ledger_unchanged(string faultTable)
    {
        var w = await WorldAsync($"atom-p-{faultTable}");
        var receipt = await (await PostAsync(w, "/api/v1/inventory/receive", Guid.NewGuid(), Receipt(w))).ExpectAsync(HttpStatusCode.OK);
        var line = (string)receipt["lines"]![0]!["id"]!; // the 5 general units
        var before = await FootprintAsync(w);
        var key = Guid.NewGuid();
        var confirm = new { receiptLineId = line, sourceBinId = w.Staging, targetBinId = w.Storage, quantity = "5", scannedProductId = w.General };

        await using (await FaultAsync(w, faultTable))
            Assert.Equal(HttpStatusCode.InternalServerError, (await PostAsync(w, "/api/v1/putaway/confirm", key, confirm)).StatusCode);
        Assert.Equal(before, await FootprintAsync(w));
        await AssertNoFaultLeftAsync();

        var done = await (await PostAsync(w, "/api/v1/putaway/confirm", key, confirm)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Completed", (string?)done["taskStatus"]);
        Assert.Equal(1L, await ScalarAsync<long>("SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t AND movement_type = 'Putaway'", ("t", w.Tenant)));
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, HttpClient Client, string Warehouse, string Staging, string Storage, string Each, string General, string Lotted,
        string Serialized, string Order, string GenLine, string LotLine, string SerLine);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label[..Math.Min(label.Length, 30)]);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "WarehouseManager");
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "Receiver");
        async Task<string> Create(string path, object body) => (string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!;

        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" });
        var zone = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "AMB", name = "Ambient" });
        var staging = await Create("/api/v1/bins", new { zoneId = zone, code = "STG", kind = "Staging" });
        var storage = await Create("/api/v1/bins", new { zoneId = zone, code = "A-01", kind = "Storage" });
        var each = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        object Product(string sku, string tracking) => new { sku, name = sku, handlingProfile = "General", trackingMode = tracking, baseUomId = each, basePrecision = 0 };
        var general = await Create("/api/v1/products", Product("GEN", "Quantity"));
        var lotted = await Create("/api/v1/products", Product("LOT", "Lot"));
        var serialized = await Create("/api/v1/products", Product("SER", "Serial"));
        var supplier = await Create("/api/v1/partners", new { code = "SUP", name = "Supplier", type = "Supplier" });
        var order = await (await admin.PostJsonAsync("/api/v1/purchase-orders", new
        {
            supplierId = supplier, warehouseId = warehouse, reference = "PO-A1",
            lines = new object[]
            {
                new { productId = general, quantity = "100", uomId = each },
                new { productId = lotted, quantity = "100", uomId = each },
                new { productId = serialized, quantity = "10", uomId = each },
            },
        })).ExpectAsync(HttpStatusCode.Created);
        var orderId = (string)order["id"]!;
        await (await admin.PostEmptyAsync($"/api/v1/purchase-orders/{orderId}/approve", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        var lines = order["lines"]!.AsArray().Select(l => (string)l!["id"]!).ToList();
        return new World(tenant.TenantId, admin, warehouse, staging, storage, each, general, lotted, serialized, orderId, lines[0], lines[1], lines[2]);
    }

    /// <summary>One receipt touching every kind of write: a plain line, a new lot and two new serials (10 units, 4 movements).</summary>
    private static object Receipt(World w) => new
    {
        warehouseId = w.Warehouse, purchaseOrderId = w.Order,
        items = new object[]
        {
            new { purchaseOrderLineId = w.GenLine, productId = w.General, quantity = "5", uomId = w.Each, targetBinId = w.Staging },
            new { purchaseOrderLineId = w.LotLine, productId = w.Lotted, quantity = "3", uomId = w.Each, targetBinId = w.Staging, lotNumber = "L1" },
            new { purchaseOrderLineId = w.SerLine, productId = w.Serialized, quantity = "2", uomId = w.Each, targetBinId = w.Staging, serialNumbers = new[] { "S1", "S2" } },
        },
    };

    private static Task<HttpResponseMessage> PostAsync(World w, string url, Guid key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        return w.Client.SendAsync(request);
    }

    /// <summary>Everything a stock command can write for the tenant, as one comparable string.</summary>
    private async Task<string> FootprintAsync(World w) => await ScalarAsync<string>("""
        SELECT concat_ws(' ',
          'receipts=' || (SELECT count(*) FROM ti.receipt WHERE tenant_id = @t),
          'receiptStatus=' || coalesce((SELECT string_agg(status::text || ':' || version, ',' ORDER BY id) FROM ti.receipt WHERE tenant_id = @t), '-'),
          'lines=' || (SELECT count(*) FROM ti.receipt_line WHERE tenant_id = @t),
          'lineSerials=' || (SELECT count(*) FROM ti.receipt_line_serial WHERE tenant_id = @t),
          'lots=' || (SELECT count(*) FROM ti.lot WHERE tenant_id = @t),
          'serials=' || (SELECT count(*) FROM ti.serial WHERE tenant_id = @t),
          'buckets=' || (SELECT count(*) || ':' || coalesce(trim_scale(sum(on_hand_qty))::text, '0') || ':' || coalesce(sum(version), 0) FROM ti.stock_bucket WHERE tenant_id = @t),
          'bucketsByBin=' || coalesce((SELECT string_agg(bin_id::text || '=' || trim_scale(on_hand_qty)::text, ',' ORDER BY bin_id, id) FROM ti.stock_bucket WHERE tenant_id = @t), '-'),
          'movements=' || (SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t),
          'tasks=' || coalesce((SELECT string_agg(status::text || ':' || trim_scale(remaining_qty)::text || ':' || version, ',' ORDER BY id) FROM ti.warehouse_task WHERE tenant_id = @t), '-'),
          'orderLines=' || (SELECT coalesce(trim_scale(sum(received_base_qty))::text, '0') || ':' || sum(version) FROM ti.purchase_order_line WHERE tenant_id = @t),
          'order=' || (SELECT string_agg(status::text || ':' || version, ',') FROM ti.purchase_order WHERE tenant_id = @t),
          'operations=' || (SELECT count(*) FROM ti.document_operation WHERE tenant_id = @t),
          'idempotency=' || (SELECT count(*) FROM ti.idempotency_record WHERE tenant_id = @t),
          'outbox=' || (SELECT count(*) FROM ti.outbox_event WHERE tenant_id = @t),
          'audit=' || (SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t),
          'alerts=' || (SELECT count(*) FROM ti.alert WHERE tenant_id = @t))
        """, ("t", w.Tenant));

    private async Task<string> PostedAsync(World w) => await ScalarAsync<string>("""
        SELECT 'receipts=' || (SELECT count(*) FROM ti.receipt WHERE tenant_id = @t)
            || ' movements=' || (SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t)
            || ' onHand=' || (SELECT trim_scale(sum(on_hand_qty))::text FROM ti.stock_bucket WHERE tenant_id = @t)
        """, ("t", w.Tenant));

    /// <summary>Adds a trigger that fails every insert into <paramref name="table"/> for this tenant; disposing removes it.</summary>
    private async Task<IAsyncDisposable> FaultAsync(World w, string table)
    {
        var name = $"test_fault_{Guid.NewGuid():N}";
        await ExecAsync($$"""
            CREATE FUNCTION ti.{{name}}() RETURNS trigger LANGUAGE plpgsql AS $f$
            BEGIN
              IF NEW.tenant_id = '{{w.Tenant}}'::uuid THEN
                RAISE EXCEPTION 'injected fault on {{table}}';
              END IF;
              RETURN NEW;
            END $f$;
            CREATE TRIGGER {{name}} BEFORE INSERT ON ti.{{table}} FOR EACH ROW EXECUTE FUNCTION ti.{{name}}();
            """);
        return new Cleanup(async () => await ExecAsync($"DROP TRIGGER {name} ON ti.{table}; DROP FUNCTION ti.{name}();"));
    }

    private async Task AssertNoFaultLeftAsync() => Assert.Equal(0L, await ScalarAsync<long>(
        "SELECT (SELECT count(*) FROM pg_trigger WHERE tgname LIKE 'test_fault_%') + (SELECT count(*) FROM pg_proc WHERE proname LIKE 'test_fault_%')"));

    private sealed class Cleanup(Func<Task> action) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await action();
    }

    private Task<int> ExecAsync(string sql, params (string Name, object Value)[] args) => TenantProvisioning.ExecAsSuperuserAsync(fx, sql, args);

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args) =>
        (T)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, args))!;
}

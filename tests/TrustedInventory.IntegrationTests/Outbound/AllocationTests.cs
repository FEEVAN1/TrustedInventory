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

/// <summary>Allocation (OUT-002, STK-005, STK-006, DEC-053) through the API on real PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class AllocationTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "OUT-002")]
    [Trait("Requirement", "STK-006")]
    public async Task The_FEFO_worked_example_reserves_the_earliest_lot_first_and_a_second_order_gets_only_the_rest()
    {
        var w = await WorldAsync("alloc-fefo");
        var lotA = await BucketAsync(w, w.Lotted, w.A01, 100, lot: ("A", 60));
        var lotB = await BucketAsync(w, w.Lotted, w.A02, 100, lot: ("B", 90));

        var first = await OrderAsync(w, Line(w.Lotted, "150"));
        var result = await (await AllocateAsync(w, first)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["100", "50"], result["allocations"]!.AsArray().Select(a => (string)a!["quantity"]!));
        Assert.Equal("Allocated", (string?)result["status"]);
        Assert.Empty(result["shortages"]!.AsArray());

        var second = await OrderAsync(w, Line(w.Lotted, "100"));
        var rest = await (await AllocateAsync(w, second, allowPartial: true)).ExpectAsync(HttpStatusCode.OK);
        var only = Assert.Single(rest["allocations"]!.AsArray())!;
        Assert.Equal(["50", lotB], new[] { (string)only["quantity"]!, await BucketOfReservationAsync((string)only["reservationId"]!) });
        Assert.Equal("50", (string?)Assert.Single(rest["shortages"]!.AsArray())!["quantity"]);
        Assert.Equal("PartAllocated", (string?)rest["status"]);
        Assert.Equal(["100|100", "100|100"], new[] { await QtyAsync(lotA), await QtyAsync(lotB) });
    }

    [Fact]
    [Trait("Requirement", "AT-OUT-01")]
    [Trait("Requirement", "STK-006")]
    public async Task All_or_nothing_refuses_149_of_150_with_no_reservation_while_partial_takes_the_149_and_later_the_remainder()
    {
        var w = await WorldAsync("alloc-at01");
        var b1 = await BucketAsync(w, w.General, w.A01, 100);
        var b2 = await BucketAsync(w, w.General, w.A02, 49);
        var order = await OrderAsync(w, Line(w.General, "150"));

        var refused = await (await AllocateAsync(w, order)).ExpectProblemAsync(HttpStatusCode.Conflict, "INSUFFICIENT_STOCK");
        Assert.Equal("1", (string?)refused["shortages"]![0]!["quantity"]);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.reservation WHERE tenant_id = @t", w.Tenant));
        Assert.Equal(["100|0", "49|0"], new[] { await QtyAsync(b1), await QtyAsync(b2) });
        var unchanged = await OrderJsonAsync(w, order);
        Assert.Equal(["Confirmed", "0"], new[] { (string)unchanged["status"]!, (string)unchanged["lines"]![0]!["reservedQuantity"]! });

        var partial = await (await AllocateAsync(w, order, allowPartial: true)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["PartAllocated", "1"], new[] { (string)partial["status"]!, (string)partial["shortages"]![0]!["quantity"]! });
        await BucketAsync(w, w.General, w.A03, 5);
        var remainder = await (await AllocateAsync(w, order, version: (int)partial["version"]!)).ExpectAsync(HttpStatusCode.OK); // only the unreserved remainder
        Assert.Equal(["1"], remainder["allocations"]!.AsArray().Select(a => (string)a!["quantity"]!));
        Assert.Equal("Allocated", (string?)remainder["status"]);
        Assert.Equal("150", (string?)(await OrderJsonAsync(w, order))["lines"]![0]!["reservedQuantity"]);
    }

    [Fact]
    [Trait("Requirement", "OUT-002")]
    [Trait("Requirement", "STK-001")]
    [Trait("Requirement", "OPS-001")]
    public async Task Held_staged_inactive_blocked_expired_short_dated_and_incompatible_stock_is_never_allocated()
    {
        var w = await WorldAsync("alloc-exclude");
        var good = await BucketAsync(w, w.General, w.A01, 5);
        await BucketAsync(w, w.General, w.A02, 50, quality: "Hold");
        await BucketAsync(w, w.General, w.A03, 50, quality: "Quarantine");
        await BucketAsync(w, w.General, w.Staging, 50);
        var retiredBin = await BucketAsync(w, w.General, w.Retired, 50);
        var blocked = await BucketAsync(w, w.General, w.A04, 50);
        await BlockAsync(w, blocked);
        Assert.NotEqual(good, retiredBin);
        var general = await (await AllocateAsync(w, await OrderAsync(w, Line(w.General, "20")), allowPartial: true)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal([good], await BucketsOfAsync(general));
        Assert.Equal("15", (string?)general["shortages"]![0]!["quantity"]);

        // Shelf life: the line needs 30 days left at dispatch; expired and short-dated lots are skipped.
        await BucketAsync(w, w.Lotted, w.A01, 10, lot: ("OLD", -1));
        await BucketAsync(w, w.Lotted, w.A02, 10, lot: ("SHORT", 10));
        var longDated = await BucketAsync(w, w.Lotted, w.A03, 10, lot: ("LONG", 45));
        var lotted = await (await AllocateAsync(w, await OrderAsync(w, Line(w.Lotted, "30", shelfLife: 30)), allowPartial: true)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal([longDated], await BucketsOfAsync(lotted));

        // Cold-chain stock is allocated only from a compatible zone, FEFO (the schema already forbids any other rule for medical products).
        await BucketAsync(w, w.Medical, w.A01, 10, lot: ("M-AMB", 100));
        var coldLate = await BucketAsync(w, w.Medical, w.C01, 10, lot: ("M-LATE", 200), receivedDaysAgo: 30);
        var coldEarly = await BucketAsync(w, w.Medical, w.C02, 10, lot: ("M-EARLY", 120), receivedDaysAgo: 1);
        var medical = await (await AllocateAsync(w, await OrderAsync(w, Line(w.Medical, "15")), allowPartial: true)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal([coldEarly, coldLate], await BucketsOfAsync(medical)); // earliest expiry first, not oldest receipt
    }

    [Fact]
    [Trait("Requirement", "STK-005")]
    public async Task FIFO_takes_the_oldest_receipt_and_LIFO_the_newest()
    {
        var w = await WorldAsync("alloc-rotation");
        var old = await BucketAsync(w, w.General, w.A01, 5, receivedDaysAgo: 10);
        await BucketAsync(w, w.General, w.A02, 5, receivedDaysAgo: 1);
        Assert.Equal([old], await BucketsOfAsync(await (await AllocateAsync(w, await OrderAsync(w, Line(w.General, "5")))).ExpectAsync(HttpStatusCode.OK)));

        await BucketAsync(w, w.Lifo, w.A01, 5, receivedDaysAgo: 10);
        var recent = await BucketAsync(w, w.Lifo, w.A02, 5, receivedDaysAgo: 1);
        Assert.Equal([recent], await BucketsOfAsync(await (await AllocateAsync(w, await OrderAsync(w, Line(w.Lifo, "5")))).ExpectAsync(HttpStatusCode.OK)));
    }

    [Fact]
    [Trait("Requirement", "AT-STK-01")]
    [Trait("Requirement", "STK-005")]
    [Trait("Requirement", "STK-004")]
    public async Task Twenty_concurrent_requests_for_ten_against_one_hundred_never_over_reserve()
    {
        var w = await WorldAsync("alloc-at-stk01");
        var bucket = await BucketAsync(w, w.General, w.A01, 60);
        var second = await BucketAsync(w, w.General, w.A02, 40);
        var orders = new List<string>();
        for (var i = 0; i < 20; i++)
            orders.Add(await OrderAsync(w, Line(w.General, "10")));

        var responses = await Task.WhenAll(orders.Select(o => AllocateAsync(w, o)));
        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Equal(10, succeeded); // all-or-nothing: exactly the ten that fit
        Assert.Equal("100", await ScalarAsync<string>(
            "SELECT trim_scale(sum(reserved_qty))::text FROM ti.stock_bucket WHERE id = ANY(@b)", ("b", new[] { Guid.Parse(bucket), Guid.Parse(second) })));
        Assert.Equal("100", await ScalarAsync<string>("SELECT trim_scale(sum(qty))::text FROM ti.reservation WHERE tenant_id = @t AND state = 'Active'", ("t", w.Tenant)));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.outbound_order_line WHERE tenant_id = @t AND allocated_base_qty NOT IN (0, 10)", w.Tenant));
        await ReconcileAsync();
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", w.Tenant));
    }

    [Fact]
    [Trait("Requirement", "STK-002")]
    [Trait("Requirement", "STK-004")]
    [Trait("Requirement", "OPS-001")]
    public async Task Deallocation_returns_reservations_reconciliation_checks_reserved_and_an_audit_failure_blocks_allocation()
    {
        var w = await WorldAsync("alloc-dealloc");
        var bucket = await BucketAsync(w, w.General, w.A01, 10);
        var order = await OrderAsync(w, Line(w.General, "6"), Line(w.General, "3"));
        var allocated = await (await AllocateAsync(w, order)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("10|9", await QtyAsync(bucket));
        var reservations = await ItemsAsync(w.Client, $"/api/v1/reservations?orderId={order}");
        Assert.Equal(2, reservations.Count);
        Assert.All(reservations, r => Assert.Equal(["Active", w.A01], new[] { (string)r["state"]!, (string)r["binId"]! }));

        var orderJson = await OrderJsonAsync(w, order);
        var firstLine = (string)orderJson["lines"]![0]!["id"]!;
        var key = Guid.NewGuid();
        var body = new { orderLineIds = new[] { firstLine }, reason = "Customer reduced the order" };
        var released = await (await PostAsync(w, $"/api/v1/orders/{order}/deallocate", key, $"\"{allocated["version"]}\"", body)).ExpectAsync(HttpStatusCode.OK);
        Assert.Single(released["releasedReservationIds"]!.AsArray());
        Assert.Equal("PartAllocated", (string?)released["status"]);
        Assert.Equal("10|3", await QtyAsync(bucket));
        var replay = await (await PostAsync(w, $"/api/v1/orders/{order}/deallocate", key, $"\"{allocated["version"]}\"", body)).ExpectAsync(HttpStatusCode.OK);
        Assert.True(JsonNode.DeepEquals(released, replay));
        Assert.Equal("10|3", await QtyAsync(bucket));

        // Reconciliation compares reserved with live reservations: a corrupted reserved figure is caught and blocked.
        await ReconcileAsync();
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", w.Tenant));
        await ExecAsync("UPDATE ti.stock_bucket SET reserved_qty = 5 WHERE id = @b", ("b", Guid.Parse(bucket)));
        await ReconcileAsync();
        Assert.Equal("3|5", await ScalarAsync<string>(
            "SELECT trim_scale(expected_reserved_qty)::text || '|' || trim_scale(stored_reserved_qty)::text FROM ti.stock_bucket_block WHERE bucket_id = @b",
            ("b", Guid.Parse(bucket))));

        // An open audit-chain failure refuses allocation outright.
        await ExecAsync("INSERT INTO ti.alert (tenant_id, type, severity, dedupe_key, details, created_by) VALUES (@t, 'AuditChainFailure', 'High', 'audit-chain', '{}', 'test')",
            ("t", w.Tenant));
        await (await AllocateAsync(w, await OrderAsync(w, Line(w.General, "1")))).ExpectProblemAsync(HttpStatusCode.Conflict, "STOCK_BLOCKED");
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, HttpClient Client, string Warehouse, string A01, string A02, string A03, string A04, string Retired,
        string Staging, string C01, string C02, string General, string Lifo, string Lotted, string Medical, string Customer);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "WarehouseManager");
        async Task<string> Create(string path, object body) => (string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!;
        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" });
        var ambient = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "AMB", name = "Ambient" });
        var cold = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "COLD", name = "Cold", tempMinC = 2, tempMaxC = 8 });
        async Task<string> Bin(string zone, string code, string kind = "Storage") => await Create("/api/v1/bins", new { zoneId = zone, code, kind });
        var a01 = await Bin(ambient, "A-01");
        var a02 = await Bin(ambient, "A-02");
        var a03 = await Bin(ambient, "A-03");
        var a04 = await Bin(ambient, "A-04");
        var retired = await Bin(ambient, "A-99");
        var staging = await Bin(ambient, "STG", "Staging");
        var c01 = await Bin(cold, "C-01");
        var c02 = await Bin(cold, "C-02");
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var profile = await Create("/api/v1/storage-profiles", new
        {
            name = "Cold chain", minC = 2, maxC = 8, monitoringRequired = true,
            excursionPolicy = new { immediateHold = true, notes = "QA reviewed" }, reason = "allocation test",
        });
        object Product(string sku, string handling, string tracking, string rotation, string? storage = null) => new
        {
            sku, name = sku, handlingProfile = handling, trackingMode = tracking, baseUomId = unit, basePrecision = 0, rotationRule = rotation, storageProfileId = storage,
        };
        var general = await Create("/api/v1/products", Product("GEN", "General", "Quantity", "FIFO"));
        var lifo = await Create("/api/v1/products", Product("LIFO", "General", "Quantity", "LIFO"));
        var lotted = await Create("/api/v1/products", Product("LOT", "General", "Lot", "FEFO"));
        var medical = await Create("/api/v1/products", Product("MED", "Medical", "Lot", "FEFO", profile));
        await ExecAsync("UPDATE ti.bin SET active = false WHERE id = @b", ("b", Guid.Parse(retired)));
        var customer = await Create("/api/v1/partners", new { code = "CUS", name = "Customer", type = "Customer" });
        return new World(tenant.TenantId, admin, warehouse, a01, a02, a03, a04, retired, staging, c01, c02, general, lifo, lotted, medical, customer);
    }

    private static object Line(string product, string quantity, int? shelfLife = null) => new { productId = product, quantity, minRemainingShelfLifeDays = shelfLife };

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

    private static Task<HttpResponseMessage> AllocateAsync(World w, string order, bool allowPartial = false, int version = 2) =>
        PostAsync(w, $"/api/v1/orders/{order}/allocate", Guid.NewGuid(), $"\"{version}\"", new { allowPartial });

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

    /// <summary>A stock bucket written as the superuser: optional lot (number, days to expiry), quality and receive age.</summary>
    private async Task<string> BucketAsync(World w, string product, string bin, decimal onHand, (string Number, int DaysToExpiry)? lot = null,
        string quality = "Available", int receivedDaysAgo = 5)
    {
        object lotId = DBNull.Value;
        if (lot is { } l)
            lotId = await ScalarAsync<Guid>("""
                INSERT INTO ti.lot (tenant_id, product_id, handling_profile, lot_number, expiry_date, created_by)
                SELECT @t, id, handling_profile, @n, current_date + @d, 'test' FROM ti.product WHERE id = @p RETURNING id
                """, ("t", w.Tenant), ("p", Guid.Parse(product)), ("n", l.Number), ("d", l.DaysToExpiry));
        var bucket = await ScalarAsync<Guid>("""
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, quality_status, received_at, on_hand_qty, created_by)
            VALUES (@t, @w, @b, @p, @l, @q::ti.quality_status, now() - make_interval(days => @age), @qty, 'test') RETURNING id
            """, ("t", w.Tenant), ("w", Guid.Parse(w.Warehouse)), ("b", Guid.Parse(bin)), ("p", Guid.Parse(product)), ("l", lotId), ("q", quality),
            ("age", receivedDaysAgo), ("qty", onHand));
        // The matching Receipt movement keeps the ledger balanced, so reconciliation sees only what a test breaks on purpose.
        if (onHand > 0)
            await ExecAsync("""
                INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, lot_id, destination_bucket_id, base_qty,
                                               document_type, document_id, actor_subject, occurred_at)
                VALUES (@t, 'Receipt', 'test-receipt:' || gen_random_uuid(), @p, @l, @b, @q, 'Receipt', gen_random_uuid(), 'test', now())
                """, ("t", w.Tenant), ("p", Guid.Parse(product)), ("l", lotId), ("b", bucket), ("q", onHand));
        return bucket.ToString();
    }

    private async Task BlockAsync(World w, string bucket)
    {
        var alert = await ScalarAsync<Guid>("""
            INSERT INTO ti.alert (tenant_id, type, severity, dedupe_key, details, created_by)
            VALUES (@t, 'ReconciliationMismatch', 'High', 'ledger-reconciliation', '{}', 'test') RETURNING id
            """, ("t", w.Tenant));
        await ExecAsync("INSERT INTO ti.stock_bucket_block (tenant_id, bucket_id, alert_id, expected_qty, stored_qty, created_by) VALUES (@t, @b, @a, 1, 50, 'test')",
            ("t", w.Tenant), ("b", Guid.Parse(bucket)), ("a", alert));
    }

    private async Task<string[]> BucketsOfAsync(JsonObject result)
    {
        var buckets = new List<string>();
        foreach (var a in result["allocations"]!.AsArray())
            buckets.Add(await BucketOfReservationAsync((string)a!["reservationId"]!));
        return buckets.ToArray();
    }

    private async Task<string> BucketOfReservationAsync(string reservation) =>
        (await ScalarAsync<Guid>("SELECT bucket_id FROM ti.reservation WHERE id = @r", ("r", Guid.Parse(reservation)))).ToString();

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

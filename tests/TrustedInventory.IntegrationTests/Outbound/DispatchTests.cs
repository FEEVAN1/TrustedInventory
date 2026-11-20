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

/// <summary>Dispatch, post-pick blocks, rework and reservation expiry (OUT-005, OUT-006, STK-008, DEC-055) on real PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class DispatchTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "AT-STK-03")]
    [Trait("Requirement", "STK-002")]
    [Trait("Requirement", "OUT-005")]
    [Trait("Requirement", "STK-004")]
    public async Task Picking_30_of_100_keeps_warehouse_stock_and_dispatch_leaves_70_with_nothing_reserved()
    {
        var w = await WorldAsync("dispatch-arith");
        var source = await BucketAsync(w, w.General, w.A01, 100);
        var order = await OrderAsync(w, Line(w.General, "30"));
        await (await AllocateAsync(w, order)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("100|30", await WarehouseTotalsAsync(w));
        var pick = await PickAllAsync(w, order, w.A01, w.General);
        Assert.Equal("100|30", await WarehouseTotalsAsync(w)); // AT-STK-03: picking never reduces warehouse stock
        Assert.Equal("70|0", await QtyAsync(source));

        var result = await (await DispatchAsync(w, order, Guid.NewGuid(), [pick], "DSP-0001")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("70|0", await WarehouseTotalsAsync(w));
        Assert.Equal("Dispatched", (string?)result["status"]);
        var line = Assert.Single(result["lines"]!.AsArray())!;
        Assert.Equal(["30", "0"], new[] { line["quantity"]!.ToString(), line["returnedQuantity"]!.ToString() });

        var shipment = await (await w.Client.GetAsync($"/api/v1/shipments/{result["shipmentId"]}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["DSP-0001", "Dispatched", w.Customer, "1"],
            new[] { (string)shipment["dispatchReference"]!, (string)shipment["status"]!, (string)shipment["customerId"]!, shipment["version"]!.ToString() });
        Assert.Single(await ItemsAsync(w.Client, $"/api/v1/shipments?orderId={order}"));
        Assert.Equal("Consumed", await ScalarAsync<string>(
            "SELECT state::text FROM ti.reservation r JOIN ti.outbound_order_line l ON l.id = r.order_line_id WHERE l.order_id = @o",
            ("o", Guid.Parse(order))));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t AND movement_type = 'Dispatch' AND reservation_id IS NOT NULL",
            w.Tenant));
        await ReconcileAsync();
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", w.Tenant));
    }

    [Fact]
    [Trait("Requirement", "AT-STK-02")]
    [Trait("Requirement", "STK-007")]
    public async Task A_dispatch_replay_after_timeout_posts_once_and_a_changed_payload_or_reused_reference_is_refused()
    {
        var w = await WorldAsync("dispatch-replay");
        await BucketAsync(w, w.General, w.A01, 10);
        var order = await OrderAsync(w, Line(w.General, "4"));
        await (await AllocateAsync(w, order)).ExpectAsync(HttpStatusCode.OK);
        var pick = await PickAllAsync(w, order, w.A01, w.General);
        var version = await OrderVersionAsync(w, order);

        var key = Guid.NewGuid();
        var first = await (await DispatchAsync(w, order, key, [pick], "DSP-R1", version)).ExpectAsync(HttpStatusCode.OK);
        var replay = await (await DispatchAsync(w, order, key, [pick], "DSP-R1", version)).ExpectAsync(HttpStatusCode.OK);
        Assert.True(JsonNode.DeepEquals(first, replay));
        await (await DispatchAsync(w, order, key, [pick], "DSP-R2", version)).ExpectProblemAsync(HttpStatusCode.Conflict, "IDEMPOTENCY_MISMATCH");

        // After the 7-day record is gone the permanent operation key still answers with the original shipment.
        await ExecAsync("DELETE FROM ti.idempotency_record WHERE tenant_id = @t", ("t", w.Tenant));
        var late = await (await DispatchAsync(w, order, key, [pick], "DSP-R1", version)).ExpectAsync(HttpStatusCode.OK);
        Assert.True(JsonNode.DeepEquals(first, late));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.shipment WHERE tenant_id = @t", w.Tenant));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t AND movement_type = 'Dispatch'", w.Tenant));
    }

    [Fact]
    [Trait("Requirement", "OUT-005")]
    public async Task By_default_dispatch_needs_the_whole_order_picked_and_partial_dispatch_ships_only_the_named_picks()
    {
        var w = await WorldAsync("dispatch-partial");
        await BucketAsync(w, w.General, w.A01, 10);
        await BucketAsync(w, w.Lotted, w.A02, 10, ("L-1", 90));
        var order = await OrderAsync(w, Line(w.General, "5"), Line(w.Lotted, "4"));
        await (await AllocateAsync(w, order)).ExpectAsync(HttpStatusCode.OK);
        var general = await PickAllAsync(w, order, w.A01, w.General);
        var lotted = (string)(await ItemsAsync(w.Client, $"/api/v1/picks?orderId={order}")).Single(p => (string?)p["productId"] == w.Lotted)["id"]!;
        var other = await OrderAsync(w, Line(w.General, "1"));
        await (await AllocateAsync(w, other)).ExpectAsync(HttpStatusCode.OK);
        var foreign = await PickAllAsync(w, other, w.A01, w.General);

        await (await DispatchAsync(w, order, Guid.NewGuid(), [general], "DSP-P1", await OrderVersionAsync(w, order)))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");                       // the lotted line is not picked
        await (await DispatchAsync(w, order, Guid.NewGuid(), [general, foreign], "DSP-P1", await OrderVersionAsync(w, order), allowPartial: true))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");      // another order's pick
        await (await DispatchAsync(w, order, Guid.NewGuid(), [lotted], "DSP-P1", await OrderVersionAsync(w, order), allowPartial: true))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");      // nothing staged yet
        await (await DispatchAsync(w, order, Guid.NewGuid(), [general], "DSP-P1", "\"99\"", allowPartial: true))
            .ExpectProblemAsync(HttpStatusCode.PreconditionFailed, "VERSION_CONFLICT");

        var part = await (await DispatchAsync(w, order, Guid.NewGuid(), [general], "DSP-P1", await OrderVersionAsync(w, order), allowPartial: true))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("PartDispatched", (string?)part["status"]);
        await (await DispatchAsync(w, order, Guid.NewGuid(), [general], "DSP-P1", await OrderVersionAsync(w, order), allowPartial: true))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");                       // reference already used

        await (await PostAsync(w, $"/api/v1/picks/{lotted}/confirm", Guid.NewGuid(), "\"1\"", Scan(w.A02, w.Lotted, "4", w.Staging, "L-1")))
            .ExpectAsync(HttpStatusCode.OK);
        var rest = await (await DispatchAsync(w, order, Guid.NewGuid(), [lotted], "DSP-P2", await OrderVersionAsync(w, order))).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Dispatched", (string?)rest["status"]);
        Assert.Equal(2, (await ItemsAsync(w.Client, $"/api/v1/shipments?orderId={order}")).Count);
    }

    [Fact]
    [Trait("Requirement", "OUT-006")]
    [Trait("Requirement", "AT-OUT-02")]
    [Trait("Requirement", "OUT-005")]
    [Trait("Requirement", "STK-001")]
    public async Task A_hold_or_expiry_after_picking_blocks_dispatch_posts_nothing_and_opens_one_rework_task()
    {
        var w = await WorldAsync("dispatch-hold");
        await BucketAsync(w, w.Lotted, w.A01, 10, ("L-H", 30));
        var order = await OrderAsync(w, Line(w.Lotted, "4"));
        await (await AllocateAsync(w, order)).ExpectAsync(HttpStatusCode.OK);
        var pick = await PickAllAsync(w, order, w.A01, w.Lotted, "L-H");
        var staging = await BucketInAsync(w.Staging, w.Lotted);

        // Post-pick hold (AT-OUT-02 hold variant): 409, no shipment, the affected task and a rework task.
        await ExecAsync("UPDATE ti.stock_bucket SET quality_status = 'Hold' WHERE id = @b", ("b", Guid.Parse(staging)));
        var problem = await (await DispatchAsync(w, order, Guid.NewGuid(), [pick], "DSP-H1", await OrderVersionAsync(w, order)))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "STOCK_BLOCKED");
        Assert.Equal(pick, (string?)Assert.Single(problem["affectedPicks"]!.AsArray())!["pickId"]);
        var rework = (string)Assert.Single(problem["reworkTaskIds"]!.AsArray())!;
        Assert.Equal("Rework|Open|Pick|4", await ScalarAsync<string>(
            "SELECT type::text || '|' || status::text || '|' || related_type || '|' || trim_scale(task_qty)::text FROM ti.warehouse_task WHERE id = @id AND related_id = @p",
            ("id", Guid.Parse(rework)), ("p", Guid.Parse(pick))));
        Assert.Equal("4|4", await QtyAsync(staging));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.shipment WHERE tenant_id = @t", w.Tenant));

        // Expired on its expiry day in the warehouse time zone: blocked too, still one open rework task.
        await ExecAsync("UPDATE ti.stock_bucket SET quality_status = 'Available' WHERE id = @b", ("b", Guid.Parse(staging)));
        await SetExpiryAsync(w.Tenant, "current_date");
        problem = await (await DispatchAsync(w, order, Guid.NewGuid(), [pick], "DSP-H1", await OrderVersionAsync(w, order)))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "STOCK_BLOCKED");
        Assert.Contains("expired", (string?)problem["affectedPicks"]![0]!["cause"], StringComparison.Ordinal);
        Assert.Equal(rework, (string?)Assert.Single(problem["reworkTaskIds"]!.AsArray()));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.warehouse_task WHERE tenant_id = @t AND type = 'Rework'", w.Tenant));

        // Once the block clears the dispatch goes through and completes the rework task.
        await SetExpiryAsync(w.Tenant, "current_date + 30");
        await (await DispatchAsync(w, order, Guid.NewGuid(), [pick], "DSP-H1", await OrderVersionAsync(w, order))).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Completed", await ScalarAsync<string>("SELECT status::text FROM ti.warehouse_task WHERE id = @id", ("id", Guid.Parse(rework))));

        // Unpicking the blocked stock works the rework task off instead.
        var second = await OrderAsync(w, Line(w.Lotted, "2"));
        await (await AllocateAsync(w, second)).ExpectAsync(HttpStatusCode.OK);
        var secondPick = await PickAllAsync(w, second, w.A01, w.Lotted, "L-H");
        await ExecAsync("UPDATE ti.stock_bucket SET quality_status = 'Hold' WHERE id = @b", ("b", Guid.Parse(staging)));
        problem = await (await DispatchAsync(w, second, Guid.NewGuid(), [secondPick], "DSP-H2", await OrderVersionAsync(w, second)))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "STOCK_BLOCKED");
        var secondRework = (string)problem["reworkTaskIds"]![0]!;
        await (await PostAsync(w, $"/api/v1/picks/{secondPick}/unpick", Guid.NewGuid(), "\"2\"",
            new { quantity = "2", targetBinId = w.A02, reason = "Held after picking; segregated for QA" })).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Completed", await ScalarAsync<string>("SELECT status::text FROM ti.warehouse_task WHERE id = @id", ("id", Guid.Parse(secondRework))));
    }

    [Fact]
    [Trait("Requirement", "STK-008")]
    [Trait("Requirement", "STK-004")]
    public async Task The_expiry_worker_releases_only_due_unpicked_reservations_and_never_races_dispatch()
    {
        var w = await WorldAsync("dispatch-expiry");
        var source = await BucketAsync(w, w.General, w.A01, 100);
        var active = await OrderAsync(w, Line(w.General, "5"));
        var picked = await OrderAsync(w, Line(w.General, "3"));
        var partly = await OrderAsync(w, Line(w.General, "6"));
        foreach (var order in new[] { active, picked, partly })
            await (await AllocateAsync(w, order)).ExpectAsync(HttpStatusCode.OK);
        var pickedPick = await PickAllAsync(w, picked, w.A01, w.General);
        var partlyPick = (string)Assert.Single(await ItemsAsync(w.Client, $"/api/v1/picks?orderId={partly}"))["id"]!;
        await (await PostAsync(w, $"/api/v1/picks/{partlyPick}/confirm", Guid.NewGuid(), "\"1\"", Scan(w.A01, w.General, "2", w.Staging, null)))
            .ExpectAsync(HttpStatusCode.OK);
        var staging = await BucketInAsync(w.Staging, w.General);
        Assert.Equal("95|9", await QtyAsync(source));

        // An order whose bucket was corrupted (awaiting reconciliation) cannot be released; it must not stop the others.
        var brokenBucket = await BucketAsync(w, w.Lotted, w.A02, 10, ("L-X", 90));
        var broken = await OrderAsync(w, Line(w.Lotted, "3"));
        await (await AllocateAsync(w, broken)).ExpectAsync(HttpStatusCode.OK);
        await ExecAsync("UPDATE ti.stock_bucket SET reserved_qty = 0 WHERE id = @b", ("b", Guid.Parse(brokenBucket)));

        await ExpireAsync(TimeSpan.FromHours(23)); // not due yet
        Assert.Equal("95|9", await QtyAsync(source));

        await ExpireAsync(TimeSpan.FromHours(25));
        Assert.Equal("95|0", await QtyAsync(source));
        Assert.Equal("5|5", await QtyAsync(staging)); // picked stock never expires
        Assert.Equal("Active", await ScalarAsync<string>(
            "SELECT state::text FROM ti.reservation r JOIN ti.outbound_order_line l ON l.id = r.order_line_id WHERE l.order_id = @o", ("o", Guid.Parse(broken))));
        await ExecAsync("UPDATE ti.stock_bucket SET reserved_qty = 3 WHERE id = @b", ("b", Guid.Parse(brokenBucket))); // repaired: retried next run
        await ExpireAsync(TimeSpan.FromHours(25));
        Assert.Equal("10|0", await QtyAsync(brokenBucket));
        Assert.Equal("Confirmed", (string?)(await OrderJsonAsync(w, active))["status"]);
        Assert.Equal("Expired", await ScalarAsync<string>(
            "SELECT state::text FROM ti.reservation r JOIN ti.outbound_order_line l ON l.id = r.order_line_id WHERE l.order_id = @o", ("o", Guid.Parse(active))));
        Assert.Equal("Cancelled", await ScalarAsync<string>(
            "SELECT p.status::text FROM ti.pick p JOIN ti.reservation r ON r.id = p.reservation_id JOIN ti.outbound_order_line l ON l.id = r.order_line_id WHERE l.order_id = @o",
            ("o", Guid.Parse(active))));
        var partTask = await (await w.Client.GetAsync($"/api/v1/picks/{partlyPick}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["2", "2", "0", "Picked"], new[]
        {
            partTask["quantity"]!.ToString(), partTask["pickedQuantity"]!.ToString(), partTask["remainingQuantity"]!.ToString(), (string)partTask["state"]!,
        });

        // Concurrent expiry runs and a dispatch: each commitment is released or consumed exactly once.
        var late = await OrderAsync(w, Line(w.General, "7"));
        await (await AllocateAsync(w, late)).ExpectAsync(HttpStatusCode.OK);
        var version = await OrderVersionAsync(w, picked);
        var dispatch = DispatchAsync(w, picked, Guid.NewGuid(), [pickedPick], "DSP-E1", version);
        await Task.WhenAll(ExpireAsync(TimeSpan.FromHours(25)), ExpireAsync(TimeSpan.FromHours(25)), ExpireAsync(TimeSpan.FromHours(25)), dispatch);
        await (await dispatch).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("95|0", await QtyAsync(source));
        Assert.Equal("2|2", await QtyAsync(staging));
        Assert.Equal(1L, await CountAsync("""
            SELECT count(*) FROM ti.audit_event WHERE tenant_id = @t AND event_type = 'ReservationsExpired'
              AND after_state ->> 'orderId' = (SELECT id::text FROM ti.outbound_order WHERE tenant_id = @t ORDER BY created_at DESC LIMIT 1)
            """, w.Tenant));
        await ReconcileAsync();
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", w.Tenant));
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, HttpClient Client, string Warehouse, string A01, string A02, string Staging, string General, string Lotted,
        string Customer);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        foreach (var role in new[] { "WarehouseManager", "Picker", "Dispatcher" })
            await TenantProvisioning.GrantSelfAsync(fx, tenant, role);
        async Task<string> Create(string path, object body) => (string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!;
        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" });
        var ambient = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "AMB", name = "Ambient" });
        async Task<string> Bin(string code, string kind = "Storage") => await Create("/api/v1/bins", new { zoneId = ambient, code, kind });
        var a01 = await Bin("A-01");
        var a02 = await Bin("A-02");
        var staging = await Bin("STG", "Staging");
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        object Product(string sku, string tracking, string rotation) => new
        {
            sku, name = sku, handlingProfile = "General", trackingMode = tracking, baseUomId = unit, basePrecision = 0, rotationRule = rotation,
        };
        var general = await Create("/api/v1/products", Product("GEN", "Quantity", "FIFO"));
        var lotted = await Create("/api/v1/products", Product("LOT", "Lot", "FEFO"));
        var customer = await Create("/api/v1/partners", new { code = "CUS", name = "Customer", type = "Customer" });
        return new World(tenant.TenantId, admin, warehouse, a01, a02, staging, general, lotted, customer);
    }

    private static object Line(string product, string quantity) => new { productId = product, quantity };

    private static object Scan(string bin, string product, string quantity, string stagingBin, string? lot) => new
    {
        quantity, scannedBinId = bin, scannedProductId = product, scannedLotNumber = lot, stagingBinId = stagingBin,
    };

    /// <summary>Confirms the order's single open pick of <paramref name="product"/> in full into staging.</summary>
    private static async Task<string> PickAllAsync(World w, string order, string bin, string product, string? lot = null)
    {
        var task = (await ItemsAsync(w.Client, $"/api/v1/picks?orderId={order}")).Single(p => (string?)p["productId"] == product);
        await (await PostAsync(w, $"/api/v1/picks/{task["id"]}/confirm", Guid.NewGuid(), $"\"{task["version"]}\"",
            Scan(bin, product, task["remainingQuantity"]!.ToString(), w.Staging, lot))).ExpectAsync(HttpStatusCode.OK);
        return (string)task["id"]!;
    }

    private static Task<HttpResponseMessage> DispatchAsync(World w, string order, Guid key, string[] picks, string reference, string? ifMatch = null,
        bool allowPartial = false) =>
        DispatchCoreAsync(w, order, key, picks, reference, ifMatch, allowPartial);

    private static async Task<HttpResponseMessage> DispatchCoreAsync(World w, string order, Guid key, string[] picks, string reference, string? ifMatch,
        bool allowPartial) =>
        await PostAsync(w, $"/api/v1/orders/{order}/dispatch", key, ifMatch ?? await OrderVersionAsync(w, order),
            new { pickIds = picks, allowPartial, dispatchReference = reference });

    private static async Task<string> OrderVersionAsync(World w, string order) => $"\"{(await OrderJsonAsync(w, order))["version"]}\"";

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

    /// <summary>A stock bucket written as the superuser with a balancing Receipt movement; optional lot.</summary>
    private async Task<string> BucketAsync(World w, string product, string bin, decimal onHand, (string Number, int DaysToExpiry)? lot = null)
    {
        object lotId = DBNull.Value;
        if (lot is { } l)
            lotId = await ScalarAsync<Guid>("""
                INSERT INTO ti.lot (tenant_id, product_id, handling_profile, lot_number, expiry_date, created_by)
                SELECT @t, id, handling_profile, @n, current_date + @d, 'test' FROM ti.product WHERE id = @p RETURNING id
                """, ("t", w.Tenant), ("p", Guid.Parse(product)), ("n", l.Number), ("d", l.DaysToExpiry));
        var bucket = await ScalarAsync<Guid>("""
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, quality_status, received_at, on_hand_qty, created_by)
            VALUES (@t, @w, @b, @p, @l, 'Available', now() - interval '5 days', @qty, 'test') RETURNING id
            """, ("t", w.Tenant), ("w", Guid.Parse(w.Warehouse)), ("b", Guid.Parse(bin)), ("p", Guid.Parse(product)), ("l", lotId), ("qty", onHand));
        await ExecAsync("""
            INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, lot_id, destination_bucket_id, base_qty,
                                           document_type, document_id, actor_subject, occurred_at)
            VALUES (@t, 'Receipt', 'test-receipt:' || gen_random_uuid(), @p, @l, @b, @q, 'Receipt', gen_random_uuid(), 'test', now())
            """, ("t", w.Tenant), ("p", Guid.Parse(product)), ("l", lotId), ("b", bucket), ("q", onHand));
        return bucket.ToString();
    }

    private async Task<string> BucketInAsync(string bin, string product) => (await ScalarAsync<Guid>(
        "SELECT id FROM ti.stock_bucket WHERE bin_id = @b AND product_id = @p AND quality_status = 'Available'",
        ("b", Guid.Parse(bin)), ("p", Guid.Parse(product)))).ToString();

    private async Task<string> QtyAsync(string bucket) => await ScalarAsync<string>(
        "SELECT trim_scale(on_hand_qty)::text || '|' || trim_scale(reserved_qty)::text FROM ti.stock_bucket WHERE id = @b", ("b", Guid.Parse(bucket)));

    private async Task<string> WarehouseTotalsAsync(World w) => await ScalarAsync<string>(
        "SELECT trim_scale(sum(on_hand_qty))::text || '|' || trim_scale(sum(reserved_qty))::text FROM ti.stock_bucket WHERE warehouse_id = @w",
        ("w", Guid.Parse(w.Warehouse)));

    private static async Task<List<JsonObject>> ItemsAsync(HttpClient client, string url) =>
        (await (await client.GetAsync(url)).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray().Select(i => i!.AsObject()).ToList();

    private async Task ExpireAsync(TimeSpan ahead)
    {
        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var runner = new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), new FakeTimeProvider(DateTimeOffset.UtcNow + ahead));
        await new ReservationExpiryJob(runner, NullLogger<ReservationExpiryJob>.Instance).RunOnceAsync(CancellationToken.None);
    }

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

    /// <summary>
    /// Stands in for time passing to the expiry day. Since 0030 (DEC-090) the lot guard refuses any expiry change without an approved
    /// correction, so this test-only arrangement switches triggers off for the one statement, as a database superuser, inside one transaction.
    /// </summary>
    private Task<int> SetExpiryAsync(Guid tenant, string expiry) => ExecAsync($"""
        BEGIN;
        SET LOCAL session_replication_role = replica;
        UPDATE ti.lot SET expiry_date = {expiry} WHERE tenant_id = @t AND lot_number = 'L-H';
        COMMIT;
        """, ("t", tenant));

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args) =>
        (T)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, args))!;
}

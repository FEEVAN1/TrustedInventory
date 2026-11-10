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

namespace TrustedInventory.IntegrationTests.Operations;

/// <summary>Bin and warehouse transfers (EXC-001, EXC-002, STK-003, AT-EXC-01, DEC-058) through the API on real PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class TransferTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "AT-EXC-01")]
    [Trait("Requirement", "EXC-001")]
    [Trait("Requirement", "STK-003")]
    [Trait("Requirement", "STK-004")]
    [Trait("Requirement", "STK-007")]
    public async Task Transferring_20_and_receiving_18_conserves_the_tenant_total_and_leaves_2_in_transit()
    {
        var w = await WorldAsync("tr-conserve");
        var source = await BucketAsync(w, w.General, w.A01, 50);
        var transfer = await CreateAsync(w, "TR-0001", (w.General, null, "20"));
        var line = (string)transfer["lines"]![0]!["id"]!;
        var approved = await (await w.Manager.PostEmptyAsync($"/api/v1/transfers/{transfer["id"]}/approve", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Approved", (string?)approved["status"]);

        var key = Guid.NewGuid();
        var dispatchBody = new { lines = new[] { new { lineId = line, sourceBucketId = source, quantity = "20" } } };
        var dispatched = await (await PostAsync(w.Manager, $"/api/v1/transfers/{transfer["id"]}/dispatch", key, "\"2\"", dispatchBody)).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["InTransit", "20", "20"], Strings(dispatched, "status", "lines.0.dispatchedQuantity", "lines.0.inTransitQuantity"));
        var replay = await (await PostAsync(w.Manager, $"/api/v1/transfers/{transfer["id"]}/dispatch", key, "\"2\"", dispatchBody)).ExpectAsync(HttpStatusCode.OK);
        Assert.True(JsonNode.DeepEquals(dispatched, replay));
        Assert.Equal("30|0", await QtyAsync(source));
        Assert.Equal("20", await InBinAsync(w.Transit1, w.General));
        Assert.Equal("50", await TenantTotalAsync(w)); // dispatch keeps stock in the tenant (STK-003)

        var received = await (await PostAsync(w.Manager, $"/api/v1/transfers/{transfer["id"]}/receive", Guid.NewGuid(), $"\"{dispatched["version"]}\"",
            new { lines = new[] { new { lineId = line, quantity = "18", stagingBinId = w.Staging2 } } })).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["PartReceived", "18", "2"], Strings(received, "status", "lines.0.receivedQuantity", "lines.0.inTransitQuantity"));
        Assert.Equal("18", await InBinAsync(w.Staging2, w.General));
        Assert.Equal("2", await InBinAsync(w.Transit1, w.General));
        Assert.Equal("50", await TenantTotalAsync(w)); // AT-EXC-01: source -20, destination +18, transit 2

        await (await PostAsync(w.Manager, $"/api/v1/transfers/{transfer["id"]}/receive", Guid.NewGuid(), $"\"{received["version"]}\"",
            new { lines = new[] { new { lineId = line, quantity = "3", stagingBinId = w.Staging2 } } })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t AND document_type = 'Transfer'", w.Tenant));
        await ReconcileAsync();
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", w.Tenant));
    }

    [Fact]
    [Trait("Requirement", "STK-003")]
    [Trait("Requirement", "IAM-003")]
    [Trait("Requirement", "SOD-1")]
    public async Task Lost_transit_stock_leaves_only_through_an_independently_approved_disposition_of_the_current_transfer()
    {
        var w = await WorldAsync("tr-dispose");
        var source = await BucketAsync(w, w.General, w.A01, 10);
        var (id, line, version) = await InTransitAsync(w, source, "TR-0002", "10");
        var afterReceipt = await (await PostAsync(w.Manager, $"/api/v1/transfers/{id}/receive", Guid.NewGuid(), version,
            new { lines = new[] { new { lineId = line, quantity = "7", stagingBinId = w.Staging2 } } })).ExpectAsync(HttpStatusCode.OK);

        // An approval binds the transfer as it stands; a later receipt makes it stale.
        var stale = await RequestApprovalAsync(w.Manager, id, (int)afterReceipt["version"]!);
        await (await Sign(w.Manager, stale, TestTokens.Issue(w.ManagerSubject))).ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_SELF"); // SOD-1
        await (await Sign(w.Approver, stale, TestTokens.Issue(w.ApproverSubject))).ExpectAsync(HttpStatusCode.OK);
        var later = await (await PostAsync(w.Manager, $"/api/v1/transfers/{id}/receive", Guid.NewGuid(), $"\"{afterReceipt["version"]}\"",
            new { lines = new[] { new { lineId = line, quantity = "1", stagingBinId = w.Staging2 } } })).ExpectAsync(HttpStatusCode.OK);
        object Dispose(string approval, string quantity) => new { lineId = line, quantity, disposition = "Lost", reason = "Fell off the truck", approvalId = approval };
        await (await PostAsync(w.Manager, $"/api/v1/transfers/{id}/disposition", Guid.NewGuid(), $"\"{later["version"]}\"", Dispose(stale, "2")))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_STALE");

        var current = await RequestApprovalAsync(w.Manager, id, (int)later["version"]!);
        await (await Sign(w.Approver, current, TestTokens.Issue(w.ApproverSubject))).ExpectAsync(HttpStatusCode.OK);
        await (await PostAsync(w.Manager, $"/api/v1/transfers/{id}/disposition", Guid.NewGuid(), $"\"{later["version"]}\"", Dispose(current, "3")))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"); // only 2 in transit
        var done = await (await PostAsync(w.Manager, $"/api/v1/transfers/{id}/disposition", Guid.NewGuid(), $"\"{later["version"]}\"", Dispose(current, "2")))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["Received", "8", "2", "0"], Strings(done, "status", "lines.0.receivedQuantity", "lines.0.disposedQuantity", "lines.0.inTransitQuantity"));
        Assert.NotNull((string?)done["receivedAt"]);
        Assert.Equal("8", await TenantTotalAsync(w));
        Assert.Equal("Consumed", (string?)(await (await w.Manager.GetAsync($"/api/v1/approvals/{current}")).ExpectAsync(HttpStatusCode.OK))["status"]);
        Assert.Equal("TransitDisposition|2|Lost: Fell off the truck", await ScalarAsync<string>("""
            SELECT movement_type::text || '|' || trim_scale(base_qty)::text || '|' || reason FROM ti.stock_movement
            WHERE tenant_id = @t AND movement_type = 'TransitDisposition'
            """, ("t", w.Tenant)));
        await ReconcileAsync();
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", w.Tenant));
    }

    [Fact]
    [Trait("Requirement", "EXC-001")]
    [Trait("Requirement", "EXC-002")]
    public async Task Transfers_follow_their_lifecycle_and_preserve_lot_and_quality_through_transit()
    {
        var w = await WorldAsync("tr-rules");
        var held = await BucketAsync(w, w.Lotted, w.A01, 6, lot: "L-7", quality: "Hold");
        var reserved = await BucketAsync(w, w.General, w.A01, 10, reserved: 8);
        await (await w.Manager.PostJsonAsync("/api/v1/transfers", new
        {
            sourceWarehouseId = w.Warehouse1, destinationWarehouseId = w.Warehouse1, reference = "TR-SAME", lines = new[] { new { productId = w.General, quantity = "1" } },
        })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");

        var transfer = await CreateAsync(w, "TR-0003", (w.Lotted, w.LotId, "6"), (w.General, null, "3"));
        await (await w.Manager.PostJsonAsync("/api/v1/transfers", new
        {
            sourceWarehouseId = w.Warehouse1, destinationWarehouseId = w.Warehouse2, reference = "TR-0003", lines = new[] { new { productId = w.General, quantity = "1" } },
        })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"); // reference taken
        var lotLine = (string)transfer["lines"]![0]!["id"]!;
        var genLine = (string)transfer["lines"]![1]!["id"]!;
        var id = (string)transfer["id"]!;
        await (await PostAsync(w.Manager, $"/api/v1/transfers/{id}/dispatch", Guid.NewGuid(), "\"1\"",
            new { lines = new[] { new { lineId = lotLine, sourceBucketId = held, quantity = "6" } } })).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE"); // not approved
        await (await w.Manager.PostEmptyAsync($"/api/v1/transfers/{id}/approve", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        await (await PostAsync(w.Manager, $"/api/v1/transfers/{id}/dispatch", Guid.NewGuid(), "\"2\"",
            new { lines = new[] { new { lineId = genLine, sourceBucketId = reserved, quantity = "3" } } })).ExpectProblemAsync(HttpStatusCode.Conflict, "INSUFFICIENT_STOCK");
        await (await PostAsync(w.Manager, $"/api/v1/transfers/{id}/dispatch", Guid.NewGuid(), "\"2\"",
            new { lines = new[] { new { lineId = genLine, sourceBucketId = held, quantity = "3" } } })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"); // wrong product
        await (await PostAsync(w.Manager, $"/api/v1/transfers/{id}/dispatch", Guid.NewGuid(), "\"2\"",
            new { lines = new[] { new { lineId = lotLine, sourceBucketId = held, quantity = "7" } } })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"); // over the line

        var dispatched = await (await PostAsync(w.Manager, $"/api/v1/transfers/{id}/dispatch", Guid.NewGuid(), "\"2\"",
            new { lines = new[] { new { lineId = lotLine, sourceBucketId = held, quantity = "6" } } })).ExpectAsync(HttpStatusCode.OK);
        await (await w.Manager.PostJsonAsync($"/api/v1/transfers/{id}/cancel", new { reason = "Changed plan" }, $"\"{dispatched["version"]}\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE"); // nothing can be cancelled once stock moved
        await (await PostAsync(w.Manager, $"/api/v1/transfers/{id}/receive", Guid.NewGuid(), $"\"{dispatched["version"]}\"",
            new { lines = new[] { new { lineId = lotLine, quantity = "6", stagingBinId = w.Storage2 } } })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"); // staging only
        await (await PostAsync(w.Manager, $"/api/v1/transfers/{id}/receive", Guid.NewGuid(), $"\"{dispatched["version"]}\"",
            new { lines = new[] { new { lineId = lotLine, quantity = "6", stagingBinId = w.Staging2 } } })).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("6|L-7|Hold", await ScalarAsync<string>("""
            SELECT trim_scale(b.on_hand_qty)::text || '|' || l.lot_number || '|' || b.quality_status::text
            FROM ti.stock_bucket b JOIN ti.lot l ON l.id = b.lot_id WHERE b.bin_id = @bin
            """, ("bin", Guid.Parse(w.Staging2))));

        var draft = await CreateAsync(w, "TR-0004", (w.General, null, "1"));
        var cancelled = await (await w.Manager.PostJsonAsync($"/api/v1/transfers/{draft["id"]}/cancel", new { reason = "Not needed" }, "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Cancelled", (string?)cancelled["status"]);
    }

    [Fact]
    [Trait("Requirement", "EXC-002")]
    [Trait("Requirement", "STK-003")]
    [Trait("Requirement", "STK-007")]
    public async Task A_bin_transfer_moves_only_unreserved_stock_to_a_suitable_bin_and_keeps_its_quality()
    {
        var w = await WorldAsync("tr-bins");
        var source = await BucketAsync(w, w.General, w.A01, 10, reserved: 4);
        object Move(string bucket, string bin, string quantity) => new { sourceBucketId = bucket, targetBinId = bin, quantity, reason = "Re-slotting" };
        await (await PostAsync(w.Manager, "/api/v1/inventory/bin-transfers", Guid.NewGuid(), "", Move(source, w.A02, "7")))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INSUFFICIENT_STOCK");
        await (await PostAsync(w.Manager, "/api/v1/inventory/bin-transfers", Guid.NewGuid(), "", Move(source, w.Transit1, "1")))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");

        var key = Guid.NewGuid();
        var moved = await (await PostAsync(w.Manager, "/api/v1/inventory/bin-transfers", key, "", Move(source, w.A02, "6"))).ExpectAsync(HttpStatusCode.OK);
        var replay = await (await PostAsync(w.Manager, "/api/v1/inventory/bin-transfers", key, "", Move(source, w.A02, "6"))).ExpectAsync(HttpStatusCode.OK);
        Assert.True(JsonNode.DeepEquals(moved, replay));
        Assert.Equal("4|4", await QtyAsync(source));
        Assert.Equal("6|0", await QtyAsync((string)moved["targetBucketId"]!));

        var cold = await BucketAsync(w, w.Medical, w.C01, 3, lot: "M-1");
        await (await PostAsync(w.Manager, "/api/v1/inventory/bin-transfers", Guid.NewGuid(), "", Move(cold, w.A02, "1")))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "INCOMPATIBLE_BIN"); // cold-chain stock stays cold (DEC-012)
        var quarantined = await BucketAsync(w, w.General, w.Q01, 2, quality: "Quarantine");
        await (await PostAsync(w.Manager, "/api/v1/inventory/bin-transfers", Guid.NewGuid(), "", Move(quarantined, w.Staging1, "1")))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"); // not into an Available-only bin
        var stillQuarantined = await (await PostAsync(w.Manager, "/api/v1/inventory/bin-transfers", Guid.NewGuid(), "", Move(quarantined, w.A02, "2")))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Quarantine", await ScalarAsync<string>("SELECT quality_status::text FROM ti.stock_bucket WHERE id = @b",
            ("b", Guid.Parse((string)stillQuarantined["targetBucketId"]!))));
        await ExecAsync("UPDATE ti.stock_bucket SET reserved_qty = 0 WHERE id = @b", ("b", Guid.Parse(source))); // the test reserved without reservations
        await ReconcileAsync();
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", w.Tenant));
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, HttpClient Manager, string ManagerSubject, HttpClient Approver, string ApproverSubject, string Warehouse1,
        string Warehouse2, string A01, string A02, string C01, string Q01, string Staging1, string Transit1, string Staging2, string Storage2, string General,
        string Lotted, string LotId, string Medical);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        foreach (var role in new[] { "WarehouseManager", "Receiver", "InventoryApprover" })
            await TenantProvisioning.GrantSelfAsync(fx, tenant, role);
        async Task<string> Create(string path, object body) => (string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!;
        var w1 = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" });
        var w2 = await Create("/api/v1/warehouses", new { code = "W2", name = "Branch", timeZone = "UTC" });
        var amb1 = await Create("/api/v1/zones", new { warehouseId = w1, code = "AMB", name = "Ambient" });
        var cold1 = await Create("/api/v1/zones", new { warehouseId = w1, code = "COLD", name = "Cold", tempMinC = 2, tempMaxC = 8 });
        var amb2 = await Create("/api/v1/zones", new { warehouseId = w2, code = "AMB", name = "Ambient" });
        async Task<string> Bin(string zone, string code, string kind = "Storage") => await Create("/api/v1/bins", new { zoneId = zone, code, kind });
        var a01 = await Bin(amb1, "A-01");
        var a02 = await Bin(amb1, "A-02");
        var c01 = await Bin(cold1, "C-01");
        var q01 = await Bin(amb1, "Q-01", "Quarantine");
        var staging1 = await Bin(amb1, "STG", "Staging");
        var staging2 = await Bin(amb2, "STG", "Staging");
        var storage2 = await Bin(amb2, "B-01");
        var transit1 = (await ScalarAsync<Guid>("SELECT id FROM ti.bin WHERE warehouse_id = @w AND kind = 'Transit'", ("w", Guid.Parse(w1)))).ToString();
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var profile = await Create("/api/v1/storage-profiles", new
        {
            name = "Cold chain", minC = 2, maxC = 8, monitoringRequired = true, excursionPolicy = new { immediateHold = true, notes = "QA reviewed" }, reason = "test",
        });
        object Product(string sku, string handling, string tracking, string? storage = null) =>
            new { sku, name = sku, handlingProfile = handling, trackingMode = tracking, baseUomId = unit, basePrecision = 0, storageProfileId = storage };
        var general = await Create("/api/v1/products", Product("GEN", "General", "Quantity"));
        var lotted = await Create("/api/v1/products", Product("LOT", "General", "Lot"));
        var medical = await Create("/api/v1/products", Product("MED", "Medical", "Lot", profile));
        var lotId = (await ScalarAsync<Guid>("""
            INSERT INTO ti.lot (tenant_id, product_id, handling_profile, lot_number, expiry_date, created_by) VALUES (@t, @p, 'General', 'L-7', current_date + 90, 'test')
            RETURNING id
            """, ("t", tenant.TenantId), ("p", Guid.Parse(lotted)))).ToString();
        var approver = $"user-approver-{Guid.NewGuid():N}";
        var membership = await ScalarAsync<Guid>("INSERT INTO ti.membership (tenant_id, user_subject, created_by) VALUES (@t, @s, 'seed') RETURNING id",
            ("t", tenant.TenantId), ("s", approver));
        await ExecAsync("""
            INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
            VALUES (@t, @m, @s, 'InventoryApprover', 'test seed', 'seed', 'seed')
            """, ("t", tenant.TenantId), ("m", membership), ("s", approver));
        return new World(tenant.TenantId, admin, tenant.Admin, fx.Client(approver, tenant.TenantId), approver, w1, w2, a01, a02, c01, q01, staging1, transit1,
            staging2, storage2, general, lotted, lotId, medical);
    }

    private static async Task<JsonObject> CreateAsync(World w, string reference, params (string Product, string? Lot, string Quantity)[] lines) =>
        await (await w.Manager.PostJsonAsync("/api/v1/transfers", new
        {
            sourceWarehouseId = w.Warehouse1, destinationWarehouseId = w.Warehouse2, reference,
            lines = lines.Select(l => new { productId = l.Product, lotId = l.Lot, quantity = l.Quantity }).ToArray(),
        })).ExpectAsync(HttpStatusCode.Created);

    /// <summary>Creates, approves and fully dispatches a one-line transfer; returns its id, line and current version as an ETag.</summary>
    private static async Task<(string Id, string Line, string Version)> InTransitAsync(World w, string source, string reference, string quantity)
    {
        var transfer = await CreateAsync(w, reference, (w.General, null, quantity));
        var id = (string)transfer["id"]!;
        var line = (string)transfer["lines"]![0]!["id"]!;
        await (await w.Manager.PostEmptyAsync($"/api/v1/transfers/{id}/approve", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        var dispatched = await (await PostAsync(w.Manager, $"/api/v1/transfers/{id}/dispatch", Guid.NewGuid(), "\"2\"",
            new { lines = new[] { new { lineId = line, sourceBucketId = source, quantity } } })).ExpectAsync(HttpStatusCode.OK);
        return (id, line, $"\"{dispatched["version"]}\"");
    }

    private static async Task<string> RequestApprovalAsync(HttpClient client, string transfer, int version) =>
        (string)(await (await client.PostJsonAsync("/api/v1/approvals", new
        {
            subjectType = "TransitDisposition", subjectId = transfer, subjectVersion = version, action = "Dispose", reason = "Pallet lost in transit",
        })).ExpectAsync(HttpStatusCode.Created))["id"]!;

    private static Task<HttpResponseMessage> Sign(HttpClient signer, string approval, string reauthToken) =>
        signer.PostJsonAsync($"/api/v1/approvals/{approval}/sign", new { reauthToken, meaning = "Disposition", reason = "Carrier report checked" }, "\"1\"");

    private static string[] Strings(JsonObject o, params string[] paths) => paths.Select(p =>
    {
        JsonNode? node = o;
        foreach (var part in p.Split('.'))
            node = int.TryParse(part, out var i) ? node![i] : node![part];
        return node?.ToString() ?? "null";
    }).ToArray();

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Guid key, string ifMatch, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        if (ifMatch.Length > 0)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return client.SendAsync(request);
    }

    private async Task<string> BucketAsync(World w, string product, string bin, decimal onHand, decimal reserved = 0, string? lot = null, string quality = "Available")
    {
        object lotId = DBNull.Value;
        if (lot is not null)
            lotId = await ScalarAsync<Guid>("""
                INSERT INTO ti.lot (tenant_id, product_id, handling_profile, lot_number, expiry_date, created_by)
                SELECT @t, id, handling_profile, @n, current_date + 90, 'test' FROM ti.product WHERE id = @p
                ON CONFLICT (tenant_id, product_id, lot_number) DO UPDATE SET lot_number = EXCLUDED.lot_number RETURNING id
                """, ("t", w.Tenant), ("p", Guid.Parse(product)), ("n", lot));
        var warehouse = await ScalarAsync<Guid>("SELECT warehouse_id FROM ti.bin WHERE id = @b", ("b", Guid.Parse(bin)));
        var bucket = await ScalarAsync<Guid>("""
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, quality_status, received_at, on_hand_qty, reserved_qty, created_by)
            VALUES (@t, @w, @b, @p, @l, @q::ti.quality_status, now() - interval '5 days', @qty, @r, 'test') RETURNING id
            """, ("t", w.Tenant), ("w", warehouse), ("b", Guid.Parse(bin)), ("p", Guid.Parse(product)), ("l", lotId), ("q", quality), ("qty", onHand), ("r", reserved));
        await ExecAsync("""
            INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, lot_id, destination_bucket_id, base_qty, document_type, document_id,
                                           actor_subject, occurred_at)
            VALUES (@t, 'Receipt', 'test-receipt:' || gen_random_uuid(), @p, @l, @b, @q, 'Receipt', gen_random_uuid(), 'test', now())
            """, ("t", w.Tenant), ("p", Guid.Parse(product)), ("l", lotId), ("b", bucket), ("q", onHand));
        return bucket.ToString();
    }

    private async Task<string> QtyAsync(string bucket) => await ScalarAsync<string>(
        "SELECT trim_scale(on_hand_qty)::text || '|' || trim_scale(reserved_qty)::text FROM ti.stock_bucket WHERE id = @b", ("b", Guid.Parse(bucket)));

    private async Task<string> InBinAsync(string bin, string product) => await ScalarAsync<string>(
        "SELECT coalesce(trim_scale(sum(on_hand_qty)), 0)::text FROM ti.stock_bucket WHERE bin_id = @b AND product_id = @p", ("b", Guid.Parse(bin)), ("p", Guid.Parse(product)));

    private async Task<string> TenantTotalAsync(World w) => await ScalarAsync<string>(
        "SELECT coalesce(trim_scale(sum(on_hand_qty)), 0)::text FROM ti.stock_bucket WHERE tenant_id = @t AND product_id = @p", ("t", w.Tenant), ("p", Guid.Parse(w.General)));

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

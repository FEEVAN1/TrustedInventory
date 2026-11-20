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

/// <summary>Customer returns (EXC-005, AT-EXC-03, DEC-060) through the API on real PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class ReturnTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "AT-EXC-03")]
    [Trait("Requirement", "EXC-005")]
    [Trait("Requirement", "STK-007")]
    [Trait("Requirement", "STK-004")]
    public async Task A_return_beyond_net_shipped_is_rejected_without_increasing_stock_and_everything_enters_quarantine()
    {
        var w = await WorldAsync("ret-limit");
        var line = await ShipmentLineAsync(w, w.General, 5);
        await (await Return(w, Guid.NewGuid(), line, "1", w.A01)).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"); // not a quarantine bin

        var key = Guid.NewGuid();
        var first = await (await Return(w, key, line, "3", w.Q01)).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(["Received", "3", "0"], new[] { (string)first["status"]!, (string)first["quantity"]!, (string)first["inspectedQuantity"]! });
        var replay = await (await Return(w, key, line, "3", w.Q01)).ExpectAsync(HttpStatusCode.Created);
        Assert.True(JsonNode.DeepEquals(first, replay));
        Assert.Equal("3|Quarantine", await InBinAsync(w.Q01, w.General)); // general goods also enter Quarantine (EXC-005)

        // AT-EXC-03: 3 + 3 > 5 is refused and adds nothing.
        await (await Return(w, Guid.NewGuid(), line, "3", w.Q01)).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "RETURN_EXCEEDS_SHIPPED");
        Assert.Equal("3|Quarantine", await InBinAsync(w.Q01, w.General));
        await (await Return(w, Guid.NewGuid(), line, "2", w.Q01)).ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("5", await ScalarAsync<string>("SELECT trim_scale(returned_qty)::text FROM ti.shipment_line WHERE id = @l", ("l", Guid.Parse(line))));

        // Concurrent returns against the last 3 units: the line lock lets only what fits through.
        var other = await ShipmentLineAsync(w, w.General, 3);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Return(w, Guid.NewGuid(), other, "2", w.Q01)));
        Assert.Equal(1, attempts.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal("2", await ScalarAsync<string>("SELECT trim_scale(returned_qty)::text FROM ti.shipment_line WHERE id = @l", ("l", Guid.Parse(other))));
        Assert.Equal("7|Quarantine", await InBinAsync(w.Q01, w.General));
        await ReconcileAsync();
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", w.Tenant));
    }

    [Fact]
    [Trait("Requirement", "EXC-005")]
    [Trait("Requirement", "STK-004")]
    [Trait("Requirement", "SOD-2")]
    [Trait("Requirement", "IAM-003")]
    public async Task Inspections_split_a_return_into_release_damage_supplier_return_and_disposal_and_medical_release_waits_for_QA()
    {
        var w = await WorldAsync("ret-inspect");
        var line = await ShipmentLineAsync(w, w.General, 5);
        var ret = await (await Return(w, Guid.NewGuid(), line, "5", w.Q01)).ExpectAsync(HttpStatusCode.Created);
        var id = (string)ret["id"]!;
        await (await Inspect(w.Manager, id, "1", "Release", "1", w.A01)).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN"); // inspectors only

        var version = 1;
        async Task<JsonObject> Decide(string disposition, string quantity, string? bin = null)
        {
            var body = await (await Inspect(w.Inspector, id, version.ToString(System.Globalization.CultureInfo.InvariantCulture), disposition, quantity, bin))
                .ExpectAsync(HttpStatusCode.OK);
            version = (int)body["version"]!;
            return body;
        }
        await Decide("Release", "2", w.A01);
        await Decide("Damaged", "1");
        await Decide("SupplierReturn", "1");
        await (await Inspect(w.Inspector, id, $"{version}", "Disposal", "2", null)).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"); // only 1 left
        var done = await Decide("Disposal", "1");
        Assert.Equal(["Inspected", "Disposal", "5"], new[] { (string)done["status"]!, (string)done["disposition"]!, (string)done["inspectedQuantity"]! });
        await (await Inspect(w.Inspector, id, $"{version}", "Disposal", "1", null)).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");

        Assert.Equal("2|Available", await InBinAsync(w.A01, w.General));
        Assert.Equal("1|Damaged", await InBinAsync(w.Q01, w.General, "Damaged"));
        Assert.Equal("0|Quarantine", await InBinAsync(w.Q01, w.General, "Quarantine"));
        Assert.Equal("QualityChange:2,QualityChange:1,SupplierReturn:1,Disposal:1", await ScalarAsync<string>("""
            SELECT string_agg(movement_type::text || ':' || trim_scale(base_qty)::text, ',' ORDER BY recorded_at, id) FROM ti.stock_movement
            WHERE tenant_id = @t AND document_type = 'Return' AND movement_type <> 'ReturnReceipt'
            """, ("t", w.Tenant)));
        Assert.Equal(4L, await CountAsync("SELECT count(*) FROM ti.customer_return_inspection WHERE tenant_id = @t", w.Tenant));

        // Medical returns are released only with QA: quality.release, the release checks and an independent ReturnRelease approval of
        // this return, used once (DEC-091). The manager received the return and asks; the QA inspector signs and releases.
        var medicalLine = await ShipmentLineAsync(w, w.Medical, 2, lot: "M-1");
        var medicalReturn = await (await Return(w, Guid.NewGuid(), medicalLine, "2", w.ColdQuarantine)).ExpectAsync(HttpStatusCode.Created);
        var medical = (string)medicalReturn["id"]!;
        Assert.Equal(w.Medical, (string?)medicalReturn["productId"]);
        await (await Inspect(w.Inspector, medical, "1", "Release", "2", w.C01)).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        var approval = (string)(await (await w.Manager.PostJsonAsync("/api/v1/approvals", new
        {
            subjectType = "ReturnRelease", subjectId = medical, subjectVersion = 1, action = "Release", reason = "Seals intact; cold chain logger clean",
        })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        Task<HttpResponseMessage> Release(string quantity, string version) => PostAsync(w.Inspector, $"/api/v1/returns/{medical}/inspect", Guid.NewGuid(),
            $"\"{version}\"", new { disposition = "Release", quantity, targetBinId = w.C01, approvalId = approval, reason = "Released by QA" });
        await (await Release("1", "1")).ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_STALE"); // not signed yet
        await (await w.Inspector.PostJsonAsync($"/api/v1/approvals/{approval}/sign",
            new { reauthToken = TestTokens.Issue(w.InspectorSubject), meaning = "QualityRelease", reason = "Return records checked" }, "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        // Only a medical release takes an approval.
        await (await PostAsync(w.Inspector, $"/api/v1/returns/{medical}/inspect", Guid.NewGuid(), "\"1\"",
            new { disposition = "Disposal", quantity = "1", approvalId = approval, reason = "x" })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        var released = await (await Release("1", "1")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(approval, (string?)released["approvalId"]);
        Assert.Equal("1|Available", await InBinAsync(w.C01, w.Medical));
        Assert.Equal("Consumed", await ScalarAsync<string>("SELECT status::text FROM ti.approval WHERE id = @a", ("a", Guid.Parse(approval))));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM ti.customer_return_inspection WHERE tenant_id = @t AND approval_id IS NOT NULL", w.Tenant));
        await (await Release("1", $"{(int)released["version"]!}")).ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_STALE"); // used once
        await (await Inspect(w.Inspector, medical, $"{(int)released["version"]!}", "Disposal", "1", null)).ExpectAsync(HttpStatusCode.OK);
        await ReconcileAsync();
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", w.Tenant));
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, HttpClient Manager, HttpClient Inspector, string Warehouse, string A01, string Q01, string C01, string ColdQuarantine,
        string General, string Medical, string Customer, string InspectorSubject);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "WarehouseManager");
        async Task<string> Create(string path, object body) => (string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!;
        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" });
        var ambient = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "AMB", name = "Ambient" });
        var cold = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "COLD", name = "Cold", tempMinC = 2, tempMaxC = 8 });
        var a01 = await Create("/api/v1/bins", new { zoneId = ambient, code = "A-01", kind = "Storage" });
        var q01 = await Create("/api/v1/bins", new { zoneId = ambient, code = "Q-01", kind = "Quarantine" });
        var c01 = await Create("/api/v1/bins", new { zoneId = cold, code = "C-01", kind = "Storage" });
        var cq = await Create("/api/v1/bins", new { zoneId = cold, code = "CQ-01", kind = "Quarantine" });
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var profile = await Create("/api/v1/storage-profiles", new
        {
            name = "Cold chain", minC = 2, maxC = 8, monitoringRequired = true, excursionPolicy = new { immediateHold = true, notes = "QA reviewed" }, reason = "test",
        });
        var general = await Create("/api/v1/products", new { sku = "GEN", name = "GEN", handlingProfile = "General", trackingMode = "Quantity", baseUomId = unit, basePrecision = 0 });
        var medical = await Create("/api/v1/products", new
        {
            sku = "MED", name = "MED", handlingProfile = "Medical", trackingMode = "Lot", baseUomId = unit, basePrecision = 0, storageProfileId = profile,
        });
        var customer = await Create("/api/v1/partners", new { code = "CUS", name = "Customer", type = "Customer" });
        var inspector = $"user-qa-{Guid.NewGuid():N}";
        var membership = await ScalarAsync<Guid>("INSERT INTO ti.membership (tenant_id, user_subject, created_by) VALUES (@t, @s, 'seed') RETURNING id",
            ("t", tenant.TenantId), ("s", inspector));
        await ExecAsync("""
            INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
            VALUES (@t, @m, @s, 'QaOfficer', 'test seed', 'seed', 'seed')
            """, ("t", tenant.TenantId), ("m", membership), ("s", inspector));
        return new World(tenant.TenantId, admin, fx.Client(inspector, tenant.TenantId), warehouse, a01, q01, c01, cq, general, medical, customer, inspector);
    }

    /// <summary>A dispatched shipment line written as the superuser (the dispatch flow itself is covered by M3's DispatchTests).</summary>
    private async Task<string> ShipmentLineAsync(World w, string product, decimal shipped, string? lot = null)
    {
        object lotId = DBNull.Value;
        if (lot is not null)
            lotId = await ScalarAsync<Guid>("""
                INSERT INTO ti.lot (tenant_id, product_id, handling_profile, lot_number, expiry_date, created_by)
                SELECT @t, id, handling_profile, @n, current_date + 200, 'test' FROM ti.product WHERE id = @p RETURNING id
                """, ("t", w.Tenant), ("p", Guid.Parse(product)), ("n", lot));
        var order = await ScalarAsync<Guid>("""
            INSERT INTO ti.outbound_order (tenant_id, customer_id, warehouse_id, reference, planned_dispatch_date, created_by)
            VALUES (@t, @c, @w, @r, current_date, 'test') RETURNING id
            """, ("t", w.Tenant), ("c", Guid.Parse(w.Customer)), ("w", Guid.Parse(w.Warehouse)), ("r", $"SO-{Guid.NewGuid():N}"[..20]));
        var orderLine = await ScalarAsync<Guid>("""
            INSERT INTO ti.outbound_order_line (tenant_id, order_id, line_number, product_id, requested_base_qty, created_by)
            VALUES (@t, @o, 1, @p, @q, 'test') RETURNING id
            """, ("t", w.Tenant), ("o", order), ("p", Guid.Parse(product)), ("q", shipped));
        var shipment = await ScalarAsync<Guid>("""
            INSERT INTO ti.shipment (tenant_id, order_id, warehouse_id, dispatch_reference, dispatched_at, created_by)
            VALUES (@t, @o, @w, 'DSP-1', now(), 'test') RETURNING id
            """, ("t", w.Tenant), ("o", order), ("w", Guid.Parse(w.Warehouse)));
        var movement = await SeedMovementAsync(w, product);
        return (await ScalarAsync<Guid>("""
            INSERT INTO ti.shipment_line (tenant_id, shipment_id, order_line_id, product_id, lot_id, base_qty, movement_id, created_by)
            VALUES (@t, @s, @l, @p, @lot, @q, @m, 'test') RETURNING id
            """, ("t", w.Tenant), ("s", shipment), ("l", orderLine), ("p", Guid.Parse(product)), ("lot", lotId), ("q", shipped), ("m", movement))).ToString();
    }

    /// <summary>A balanced receive-then-dispatch-out pair, so the ledger has a movement for shipment lines to reference.</summary>
    private async Task<Guid> SeedMovementAsync(World w, string product)
    {
        var bucket = await ScalarAsync<Guid>("""
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, quality_status, received_at, on_hand_qty, created_by)
            VALUES (@t, @w, @b, @p, 'Available', now(), 0, 'test')
            ON CONFLICT (tenant_id, bin_id, product_id, lot_id, serial_id, quality_status) DO UPDATE SET updated_at = now() RETURNING id
            """, ("t", w.Tenant), ("w", Guid.Parse(w.Warehouse)), ("b", Guid.Parse(w.A01)), ("p", Guid.Parse(product)));
        await ExecAsync("""
            INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, destination_bucket_id, base_qty, document_type, document_id, actor_subject, occurred_at)
            VALUES (@t, 'Receipt', 'seed:' || gen_random_uuid(), @p, @b, 1, 'Receipt', gen_random_uuid(), 'test', now())
            """, ("t", w.Tenant), ("p", Guid.Parse(product)), ("b", bucket));
        return await ScalarAsync<Guid>("""
            INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, source_bucket_id, base_qty, document_type, document_id, actor_subject, occurred_at)
            VALUES (@t, 'AdjustmentDecrease', 'seed:' || gen_random_uuid(), @p, @b, 1, 'Adjustment', gen_random_uuid(), 'test', now()) RETURNING id
            """, ("t", w.Tenant), ("p", Guid.Parse(product)), ("b", bucket));
    }

    private static Task<HttpResponseMessage> Return(World w, Guid key, string shipmentLine, string quantity, string bin) =>
        PostAsync(w.Manager, "/api/v1/returns", key, "", new { shipmentLineId = shipmentLine, quantity, quarantineBinId = bin, reason = "Customer refused delivery" });

    private static Task<HttpResponseMessage> Inspect(HttpClient client, string id, string version, string disposition, string quantity, string? bin) =>
        PostAsync(client, $"/api/v1/returns/{id}/inspect", Guid.NewGuid(), $"\"{version}\"",
            new { disposition, quantity, targetBinId = bin, reason = $"Inspected: {disposition}" });

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

    private async Task<string> InBinAsync(string bin, string product, string? quality = null) => await ScalarAsync<string>("""
        SELECT coalesce(trim_scale(sum(on_hand_qty)), 0)::text || '|' || coalesce(string_agg(DISTINCT quality_status::text, '/'), @q)
        FROM ti.stock_bucket WHERE bin_id = @b AND product_id = @p AND (@q = '' OR quality_status::text = @q) AND (on_hand_qty > 0 OR @q <> '')
        """, ("b", Guid.Parse(bin)), ("p", Guid.Parse(product)), ("q", quality ?? ""));

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

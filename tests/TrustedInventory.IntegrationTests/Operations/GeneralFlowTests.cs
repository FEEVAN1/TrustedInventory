using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TrustedInventory.Application.Audit;
using TrustedInventory.Application.Operations;
using TrustedInventory.Infrastructure;
using TrustedInventory.Infrastructure.Jobs;
using TrustedInventory.Infrastructure.Persistence;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Operations;

/// <summary>
/// The M4 gate scenario (spec section 20): one general-goods flow through every M2–M4 workflow on the real API, with
/// independent approvals, ending in stock conservation (every unit accounted for) and a clean ledger reconciliation.
/// </summary>
[Collection(ApiTestGroup.Name)]
public sealed class GeneralFlowTests(ApiFixture fx)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    [Trait("Requirement", "STK-002")]
    [Trait("Requirement", "STK-003")]
    [Trait("Requirement", "STK-004")]
    [Trait("Requirement", "EXC-001")]
    [Trait("Requirement", "EXC-003")]
    [Trait("Requirement", "EXC-004")]
    [Trait("Requirement", "EXC-005")]
    [Trait("Requirement", "AUD-004")]
    public async Task Receive_store_move_transfer_count_adjust_ship_return_and_export_conserve_every_unit()
    {
        var w = await WorldAsync("e2e-general");

        // 1. Receive 100 against a purchase order and put them away (M2).
        var receipt = await SendAsync(w.Manager, "/api/v1/inventory/receive", null, new
        {
            warehouseId = w.W1, purchaseOrderId = w.Po,
            items = new[] { new { purchaseOrderLineId = w.PoLine, productId = w.General, quantity = "100", uomId = w.Each, targetBinId = w.Stg1 } },
        });
        var receiptLine = (string)receipt["lines"]![0]!["id"]!;
        await SendAsync(w.Manager, "/api/v1/putaway/confirm", null, new
        {
            receiptLineId = receiptLine, sourceBinId = w.Stg1, targetBinId = w.A01, quantity = "100", scannedProductId = w.General,
        });
        var a01 = await BucketAsync(w.A01);

        // 2. Re-slot 20 to A-02 (EXC-002).
        await SendAsync(w.Manager, "/api/v1/inventory/bin-transfers", null, new { sourceBucketId = a01, targetBinId = w.A02, quantity = "20" });

        // 3. Transfer 30 to the branch; 28 arrive and 2 are written off with an independent approval (EXC-001, STK-003).
        var transfer = await (await w.Manager.PostJsonAsync("/api/v1/transfers", new
        {
            sourceWarehouseId = w.W1, destinationWarehouseId = w.W2, reference = "TR-E2E", lines = new[] { new { productId = w.General, quantity = "30" } },
        })).ExpectAsync(HttpStatusCode.Created);
        var tid = (string)transfer["id"]!;
        var tline = (string)transfer["lines"]![0]!["id"]!;
        await (await w.Manager.PostEmptyAsync($"/api/v1/transfers/{tid}/approve", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        var sent = await SendAsync(w.Manager, $"/api/v1/transfers/{tid}/dispatch", "\"2\"", new { lines = new[] { new { lineId = tline, sourceBucketId = a01, quantity = "30" } } });
        var got = await SendAsync(w.Manager, $"/api/v1/transfers/{tid}/receive", $"\"{sent["version"]}\"",
            new { lines = new[] { new { lineId = tline, quantity = "28", stagingBinId = w.Stg2 } } });
        var lostApproval = await ApproveAsync(w, "TransitDisposition", tid, (int)got["version"]!, "Dispose");
        var settled = await SendAsync(w.Manager, $"/api/v1/transfers/{tid}/disposition", $"\"{got["version"]}\"",
            new { lineId = tline, quantity = "2", disposition = "Lost", reason = "Missing on arrival", approvalId = lostApproval });
        Assert.Equal("Received", (string?)settled["status"]);

        // 4. Count A-02: 19 of 20; the variance posts after an independent approval (EXC-003).
        var count = await (await w.Manager.PostJsonAsync("/api/v1/counts", new { warehouseId = w.W1, binIds = new[] { w.A02 }, reason = "Cycle count" }))
            .ExpectAsync(HttpStatusCode.Created);
        var cid = (string)count["id"]!;
        await (await w.Manager.PostEmptyAsync($"/api/v1/counts/{cid}/start", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        var submitted = await (await w.Manager.PostJsonAsync($"/api/v1/counts/{cid}/submit",
            new { lines = new[] { new { binId = w.A02, productId = w.General, observedQuantity = "19" } } }, "\"2\"")).ExpectAsync(HttpStatusCode.OK);
        var countApproval = await ApproveAsync(w, "CountVariance", cid, (int)submitted["version"]!, "Post");
        var approvedCount = await (await w.Manager.PostJsonAsync($"/api/v1/counts/{cid}/approve", new { approvalId = countApproval }, $"\"{submitted["version"]}\""))
            .ExpectAsync(HttpStatusCode.OK);
        await SendAsync(w.Manager, $"/api/v1/counts/{cid}/post", $"\"{approvedCount["version"]}\"", new { });

        // 5. Five units found in A-01, adjusted with an independent approval (EXC-004).
        var adjustment = await (await w.Manager.PostJsonAsync("/api/v1/adjustments", new
        {
            bucketId = a01, binId = w.A01, productId = w.General, delta = "5", reasonCode = "FOUND", narrative = "Found behind the racking",
        })).ExpectAsync(HttpStatusCode.Created);
        var aid = (string)adjustment["id"]!;
        var adjApproval = await ApproveAsync(w, "Adjustment", aid, 1, "Post");
        await SendAsync(w.Manager, $"/api/v1/adjustments/{aid}/post", "\"1\"", new { approvalId = adjApproval });

        // 6. Sell 40: allocate, pick to staging and dispatch (M3).
        var order = await (await w.Manager.PostJsonAsync("/api/v1/orders", new
        {
            customerId = w.Customer, warehouseId = w.W1, reference = "SO-E2E",
            plannedDispatchDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            lines = new[] { new { productId = w.General, quantity = "40" } },
        })).ExpectAsync(HttpStatusCode.Created);
        var oid = (string)order["id"]!;
        await (await w.Manager.PostEmptyAsync($"/api/v1/orders/{oid}/confirm", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        await SendAsync(w.Manager, $"/api/v1/orders/{oid}/allocate", "\"2\"", new { allowPartial = false });
        var picks = await ItemsAsync(w.Manager, $"/api/v1/picks?orderId={oid}");
        foreach (var pick in picks)
            await SendAsync(w.Manager, $"/api/v1/picks/{pick["id"]}/confirm", $"\"{pick["version"]}\"", new
            {
                quantity = (string)pick["remainingQuantity"]!, scannedBinId = (string)pick["sourceBinId"]!, scannedProductId = w.General, stagingBinId = w.Stg1,
            });
        var orderVersion = (await (await w.Manager.GetAsync($"/api/v1/orders/{oid}")).ExpectAsync(HttpStatusCode.OK))["version"]!.ToString();
        var shipment = await SendAsync(w.Manager, $"/api/v1/orders/{oid}/dispatch", $"\"{orderVersion}\"",
            new { pickIds = picks.Select(p => (string)p["id"]!).ToArray(), dispatchReference = "DSP-E2E" });
        Assert.Equal("Dispatched", (string?)shipment["status"]);
        var shipmentLine = (string)shipment["lines"]![0]!["id"]!;

        // 7. Four come back: three released to A-01, one disposed (EXC-005).
        var ret = await SendAsync(w.Manager, "/api/v1/returns", null, new { shipmentLineId = shipmentLine, quantity = "4", quarantineBinId = w.Q01, reason = "Over-delivered" });
        var released = await SendAsync(w.Inspector, $"/api/v1/returns/{ret["id"]}/inspect", "\"1\"",
            new { disposition = "Release", quantity = "3", targetBinId = w.A01, reason = "Sealed and undamaged" });
        await SendAsync(w.Inspector, $"/api/v1/returns/{ret["id"]}/inspect", $"\"{released["version"]}\"", new { disposition = "Disposal", quantity = "1", reason = "Crushed" });

        // Conservation: 100 received − 2 lost − 1 counted short + 5 found − 40 shipped + 4 returned − 1 disposed = 65.
        Assert.Equal("65", await ScalarAsync<string>(
            "SELECT trim_scale(sum(on_hand_qty))::text FROM ti.stock_bucket WHERE tenant_id = @t AND product_id = @p", ("t", w.Tenant), ("p", Guid.Parse(w.General))));
        Assert.Equal("28", await ScalarAsync<string>(
            "SELECT trim_scale(sum(on_hand_qty))::text FROM ti.stock_bucket WHERE tenant_id = @t AND warehouse_id = @w", ("t", w.Tenant), ("w", Guid.Parse(w.W2))));
        Assert.Equal("0", await ScalarAsync<string>(
            "SELECT trim_scale(coalesce(sum(reserved_qty), 0))::text FROM ti.stock_bucket WHERE tenant_id = @t", ("t", w.Tenant)));
        Assert.Equal(3L, await ScalarAsync<long>("SELECT count(*) FROM ti.approval WHERE tenant_id = @t AND status = 'Consumed'", ("t", w.Tenant)));
        await ReconcileAsync();
        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", ("t", w.Tenant)));

        // 8. The movement export accounts for every ledger entry (AUD-004).
        var export = await (await w.Manager.PostJsonAsync("/api/v1/exports", new { type = "Movements", filters = new { } })).ExpectAsync(HttpStatusCode.Accepted);
        await GenerateAsync();
        var url = (string)(await (await w.Manager.GetAsync($"/api/v1/exports/{export["id"]}/download-link")).ExpectAsync(HttpStatusCode.OK))["url"]!;
        var lines = (await (await w.Manager.GetAsync(url)).Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(await ScalarAsync<long>("SELECT count(*) FROM ti.stock_movement WHERE tenant_id = @t", ("t", w.Tenant)), lines.LongLength);
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, HttpClient Manager, HttpClient Approver, string ApproverSubject, HttpClient Inspector, string W1, string W2,
        string Stg1, string A01, string A02, string Q01, string Stg2, string Each, string General, string Po, string PoLine, string Customer);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.SignUpAsync(fx, label); // owner: exports.create
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        foreach (var role in new[] { "WarehouseManager", "Receiver", "Picker", "Dispatcher" })
            await TenantProvisioning.GrantSelfAsync(fx, tenant, role);
        async Task<string> Create(string path, object body) => (string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!;
        var w1 = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" });
        var w2 = await Create("/api/v1/warehouses", new { code = "W2", name = "Branch", timeZone = "UTC" });
        var z1 = await Create("/api/v1/zones", new { warehouseId = w1, code = "AMB", name = "Ambient" });
        var z2 = await Create("/api/v1/zones", new { warehouseId = w2, code = "AMB", name = "Ambient" });
        var stg1 = await Create("/api/v1/bins", new { zoneId = z1, code = "STG", kind = "Staging" });
        var a01 = await Create("/api/v1/bins", new { zoneId = z1, code = "A-01", kind = "Storage", pickPriority = 1 });
        var a02 = await Create("/api/v1/bins", new { zoneId = z1, code = "A-02", kind = "Storage", pickPriority = 2 });
        var q01 = await Create("/api/v1/bins", new { zoneId = z1, code = "Q-01", kind = "Quarantine" });
        var stg2 = await Create("/api/v1/bins", new { zoneId = z2, code = "STG", kind = "Staging" });
        var each = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var general = await Create("/api/v1/products", new { sku = "GEN", name = "GEN", handlingProfile = "General", trackingMode = "Quantity", baseUomId = each, basePrecision = 0 });
        var supplier = await Create("/api/v1/partners", new { code = "SUP", name = "Supplier", type = "Supplier" });
        var customer = await Create("/api/v1/partners", new { code = "CUS", name = "Customer", type = "Customer" });
        var po = await (await admin.PostJsonAsync("/api/v1/purchase-orders", new
        {
            supplierId = supplier, warehouseId = w1, reference = "PO-E2E", lines = new[] { new { productId = general, quantity = "100", uomId = each } },
        })).ExpectAsync(HttpStatusCode.Created);
        await (await admin.PostEmptyAsync($"/api/v1/purchase-orders/{po["id"]}/approve", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        var approver = await MemberAsync(tenant.TenantId, "approver", "InventoryApprover");
        var inspector = await MemberAsync(tenant.TenantId, "inspector", "QaOfficer");
        return new World(tenant.TenantId, admin, fx.Client(approver, tenant.TenantId), approver, fx.Client(inspector, tenant.TenantId), w1, w2, stg1, a01, a02,
            q01, stg2, each, general, (string)po["id"]!, (string)po["lines"]![0]!["id"]!, customer);
    }

    private async Task<string> MemberAsync(Guid tenant, string label, string role)
    {
        var subject = $"user-{label}-{Guid.NewGuid():N}";
        var membership = await ScalarAsync<Guid>("INSERT INTO ti.membership (tenant_id, user_subject, created_by) VALUES (@t, @s, 'seed') RETURNING id",
            ("t", tenant), ("s", subject));
        await TenantProvisioning.ExecAsSuperuserAsync(fx, """
            INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
            VALUES (@t, @m, @s, @r::ti.role_name, 'test seed', 'seed', 'seed')
            """, ("t", tenant), ("m", membership), ("s", subject), ("r", role));
        return subject;
    }

    /// <summary>Requests (as the manager) and signs (as the independent approver, with fresh re-authentication) an approval.</summary>
    private static async Task<string> ApproveAsync(World w, string subjectType, string subjectId, int version, string action)
    {
        var approval = (string)(await (await w.Manager.PostJsonAsync("/api/v1/approvals", new
        {
            subjectType, subjectId, subjectVersion = version, action, reason = $"{subjectType} for review",
        })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await w.Approver.PostJsonAsync($"/api/v1/approvals/{approval}/sign",
            new { reauthToken = TestTokens.Issue(w.ApproverSubject), meaning = "Approval", reason = "Checked" }, "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        return approval;
    }

    private static async Task<JsonObject> SendAsync(HttpClient client, string url, string? ifMatch, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Options), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        var response = await client.SendAsync(request);
        return await response.ExpectAsync(response.StatusCode == HttpStatusCode.Created ? HttpStatusCode.Created : HttpStatusCode.OK);
    }

    private async Task<string> BucketAsync(string bin) =>
        (await ScalarAsync<Guid>("SELECT id FROM ti.stock_bucket WHERE bin_id = @b AND quality_status = 'Available'", ("b", Guid.Parse(bin)))).ToString();

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

    private async Task GenerateAsync()
    {
        await using var app = DependencyInjection.BuildDataSource(fx.AppConnection);
        await using var platform = DependencyInjection.BuildDataSource(fx.PlatformConnection);
        var runner = new TenantJobRunner(new PlatformDataSource(platform), new AppDataSource(app), new FakeTimeProvider(DateTimeOffset.UtcNow));
        await new ExportGenerationJob(runner, fx.Factory.Services.GetRequiredService<IObjectStore>(),
            new CheckpointSigningOptions(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(), "test-checkpoint-key"), NullLogger<ExportGenerationJob>.Instance)
            .RunOnceAsync(CancellationToken.None);
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args) =>
        (T)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, args))!;
}

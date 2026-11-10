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

/// <summary>Stock counts and the count lock (EXC-003, AT-EXC-04, DEC-008, DEC-059) through the API on real PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class CountTests(ApiFixture fx)
{
    [Fact]
    [Trait("Requirement", "AT-EXC-04")]
    [Trait("Requirement", "EXC-003")]
    [Trait("Requirement", "IAM-003")]
    [Trait("Requirement", "SOD-1")]
    [Trait("Requirement", "STK-004")]
    public async Task A_count_locks_its_bins_counts_blind_keeps_rejected_revisions_and_posts_its_approved_variance_once()
    {
        var w = await WorldAsync("count-flow");
        var general = await BucketAsync(w, w.General, w.A01, 10);
        await BucketAsync(w, w.Lotted, w.A02, 5, lot: "L-1");
        var count = await (await w.Counter.PostJsonAsync("/api/v1/counts", new { warehouseId = w.Warehouse, binIds = new[] { w.A01, w.A02 }, reason = "Cycle count" }))
            .ExpectAsync(HttpStatusCode.Created);
        var id = (string)count["id"]!;
        var started = await (await w.Counter.PostEmptyAsync($"/api/v1/counts/{id}/start", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Counting", (string?)started["status"]);
        Assert.NotNull(started["snapshotSequence"]);

        // One lock per bin; quantity movements in a locked bin are refused (DEC-008).
        var rival = (string)(await (await w.Manager.PostJsonAsync("/api/v1/counts", new { warehouseId = w.Warehouse, binIds = new[] { w.A01 }, reason = "Spot check" }))
            .ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await w.Manager.PostEmptyAsync($"/api/v1/counts/{rival}/start", "\"1\"")).ExpectProblemAsync(HttpStatusCode.Conflict, "COUNT_LOCKED");
        await (await PostAsync(w.Manager, "/api/v1/inventory/bin-transfers", Guid.NewGuid(), "", new { sourceBucketId = general, targetBinId = w.A03, quantity = "1" }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "COUNT_LOCKED");

        // Blind count: counters see no expected quantities until they submit.
        var blind = await (await w.Counter.GetAsync($"/api/v1/counts/{id}")).ExpectAsync(HttpStatusCode.OK);
        Assert.All(blind["lines"]!.AsArray(), l => Assert.Null(l!["expectedQuantity"]));
        var sighted = await (await w.Manager.GetAsync($"/api/v1/counts/{id}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["10", "5"], sighted["lines"]!.AsArray().Select(l => (string)l!["expectedQuantity"]!).Order(StringComparer.Ordinal).ToArray());

        await (await w.Counter.PostJsonAsync($"/api/v1/counts/{id}/submit", new { lines = new[] { Observe(w.A02, w.Lotted, "5", "NOPE") } }, "\"2\""))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"); // unknown lot
        var first = await (await w.Counter.PostJsonAsync($"/api/v1/counts/{id}/submit",
            new { lines = new[] { Observe(w.A01, w.General, "8"), Observe(w.A02, w.Lotted, "5", "L-1") } }, "\"2\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Submitted", (string?)first["status"]);
        Assert.Contains(first["lines"]!.AsArray(), l => (string?)l!["variance"] == "-2");

        // Reject: a new revision; the first observations stay as history (AT-EXC-04).
        var rejected = await (await w.Manager.PostJsonAsync($"/api/v1/counts/{id}/reject", new { reason = "Recount aisle A" }, $"\"{first["version"]}\""))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["Counting", "2"], new[] { (string)rejected["status"]!, rejected["revision"]!.ToString() });
        Assert.Equal("8", await ScalarAsync<string>("SELECT trim_scale(observed_qty)::text FROM ti.stock_count_line WHERE count_id = @c AND revision = 1 AND bin_id = @b",
            ("c", Guid.Parse(id)), ("b", Guid.Parse(w.A01))));
        var second = await (await w.Counter.PostJsonAsync($"/api/v1/counts/{id}/submit", new
        {
            lines = new[] { Observe(w.A01, w.General, "7"), Observe(w.A02, w.Lotted, "5", "L-1"), Observe(w.A02, w.General, "2") }, // a find in A-02
        }, $"\"{rejected["version"]}\"")).ExpectAsync(HttpStatusCode.OK);

        // Independent approval of exactly this submission; the counter may not sign it (SOD-1).
        var approval = (string)(await (await w.Manager.PostJsonAsync("/api/v1/approvals", new
        {
            subjectType = "CountVariance", subjectId = id, subjectVersion = (int)second["version"]!, action = "Post", reason = "Variance after recount",
        })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await Sign(w.Counter, approval, TestTokens.Issue(w.CounterSubject))).ExpectProblemAsync(HttpStatusCode.Conflict, "APPROVAL_SELF");
        await (await Sign(w.Approver, approval, TestTokens.Issue(w.ApproverSubject))).ExpectAsync(HttpStatusCode.OK);
        var approved = await (await w.Manager.PostJsonAsync($"/api/v1/counts/{id}/approve", new { approvalId = approval }, $"\"{second["version"]}\""))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Approved", (string?)approved["status"]);

        var key = Guid.NewGuid();
        var posted = await (await PostAsync(w.Manager, $"/api/v1/counts/{id}/post", key, $"\"{approved["version"]}\"", new { })).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["Posted", "2"], new[] { (string)posted["status"]!, posted["movementIds"]!.AsArray().Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var replay = await (await PostAsync(w.Manager, $"/api/v1/counts/{id}/post", key, $"\"{approved["version"]}\"", new { })).ExpectAsync(HttpStatusCode.OK);
        Assert.True(JsonNode.DeepEquals(posted, replay));
        await (await PostAsync(w.Manager, $"/api/v1/counts/{id}/post", Guid.NewGuid(), $"\"{posted["version"]}\"", new { }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE"); // variance posts once
        Assert.Equal("7|0", await QtyAsync(general));
        Assert.Equal("2|Available", await ScalarAsync<string>(
            "SELECT trim_scale(on_hand_qty)::text || '|' || quality_status::text FROM ti.stock_bucket WHERE bin_id = @b AND product_id = @p",
            ("b", Guid.Parse(w.A02)), ("p", Guid.Parse(w.General))));
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM ti.adjustment WHERE tenant_id = @t AND status = 'Posted' AND count_id IS NOT NULL", w.Tenant));
        Assert.Equal("Consumed", (string?)(await (await w.Manager.GetAsync($"/api/v1/approvals/{approval}")).ExpectAsync(HttpStatusCode.OK))["status"]);

        // The bins are free again.
        await (await PostAsync(w.Manager, "/api/v1/inventory/bin-transfers", Guid.NewGuid(), "", new { sourceBucketId = general, targetBinId = w.A03, quantity = "1" }))
            .ExpectAsync(HttpStatusCode.OK);
        await ReconcileAsync();
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM ti.alert WHERE tenant_id = @t AND type = 'ReconciliationMismatch'", w.Tenant));
    }

    [Fact]
    [Trait("Requirement", "EXC-003")]
    [Trait("Requirement", "EXC-004")]
    public async Task A_shortage_never_takes_reserved_stock_and_cancelling_unlocks_while_keeping_every_observation()
    {
        var w = await WorldAsync("count-cancel");
        var bucket = await BucketAsync(w, w.General, w.A01, 10, reserved: 8);
        var id = (string)(await (await w.Counter.PostJsonAsync("/api/v1/counts", new { warehouseId = w.Warehouse, binIds = new[] { w.A01 }, reason = "Cycle count" }))
            .ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await w.Counter.PostEmptyAsync($"/api/v1/counts/{id}/start", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        var submitted = await (await w.Counter.PostJsonAsync($"/api/v1/counts/{id}/submit", new { lines = new[] { Observe(w.A01, w.General, "5") } }, "\"2\""))
            .ExpectAsync(HttpStatusCode.OK);
        var approval = (string)(await (await w.Manager.PostJsonAsync("/api/v1/approvals", new
        {
            subjectType = "CountVariance", subjectId = id, subjectVersion = (int)submitted["version"]!, action = "Post", reason = "Shortage of 5",
        })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await Sign(w.Approver, approval, TestTokens.Issue(w.ApproverSubject))).ExpectAsync(HttpStatusCode.OK);
        var approved = await (await w.Manager.PostJsonAsync($"/api/v1/counts/{id}/approve", new { approvalId = approval }, $"\"{submitted["version"]}\""))
            .ExpectAsync(HttpStatusCode.OK);

        // A shortage of 5 with only 2 unreserved is refused, and nothing changes (EXC-004).
        await (await PostAsync(w.Manager, $"/api/v1/counts/{id}/post", Guid.NewGuid(), $"\"{approved["version"]}\"", new { }))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INSUFFICIENT_STOCK");
        Assert.Equal("10|8", await QtyAsync(bucket));
        Assert.True(await ScalarAsync<bool>("SELECT lock_active FROM ti.stock_count_bin WHERE count_id = @c", ("c", Guid.Parse(id))));

        var cancelled = await (await w.Manager.PostJsonAsync($"/api/v1/counts/{id}/cancel", new { reason = "Resolve reservations first" }, $"\"{approved["version"]}\""))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Cancelled", (string?)cancelled["status"]);
        Assert.False(await ScalarAsync<bool>("SELECT lock_active FROM ti.stock_count_bin WHERE count_id = @c", ("c", Guid.Parse(id))));
        Assert.Equal("5", await ScalarAsync<string>("SELECT trim_scale(observed_qty)::text FROM ti.stock_count_line WHERE count_id = @c", ("c", Guid.Parse(id))));
        Assert.Equal("Invalidated", (string?)(await (await w.Manager.GetAsync($"/api/v1/approvals/{approval}")).ExpectAsync(HttpStatusCode.OK))["status"]);
        await (await PostAsync(w.Manager, "/api/v1/inventory/bin-transfers", Guid.NewGuid(), "", new { sourceBucketId = bucket, targetBinId = w.A03, quantity = "1" }))
            .ExpectAsync(HttpStatusCode.OK);
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, HttpClient Manager, HttpClient Counter, string CounterSubject, HttpClient Approver, string ApproverSubject,
        string Warehouse, string A01, string A02, string A03, string General, string Lotted);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        foreach (var role in new[] { "WarehouseManager", "Receiver" })
            await TenantProvisioning.GrantSelfAsync(fx, tenant, role);
        async Task<string> Create(string path, object body) => (string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!;
        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone = "UTC" });
        var zone = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "AMB", name = "Ambient" });
        var a01 = await Create("/api/v1/bins", new { zoneId = zone, code = "A-01", kind = "Storage" });
        var a02 = await Create("/api/v1/bins", new { zoneId = zone, code = "A-02", kind = "Storage" });
        var a03 = await Create("/api/v1/bins", new { zoneId = zone, code = "A-03", kind = "Storage" });
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var general = await Create("/api/v1/products", new { sku = "GEN", name = "GEN", handlingProfile = "General", trackingMode = "Quantity", baseUomId = unit, basePrecision = 0 });
        var lotted = await Create("/api/v1/products", new { sku = "LOT", name = "LOT", handlingProfile = "General", trackingMode = "Lot", baseUomId = unit, basePrecision = 0 });
        var counter = await MemberAsync(tenant.TenantId, "counter", "Receiver", "InventoryApprover"); // can count and sign, but never its own count
        var approver = await MemberAsync(tenant.TenantId, "approver", "InventoryApprover");
        return new World(tenant.TenantId, admin, fx.Client(counter, tenant.TenantId), counter, fx.Client(approver, tenant.TenantId), approver, warehouse,
            a01, a02, a03, general, lotted);
    }

    private async Task<string> MemberAsync(Guid tenant, string label, params string[] roles)
    {
        var subject = $"user-{label}-{Guid.NewGuid():N}";
        var membership = await ScalarAsync<Guid>("INSERT INTO ti.membership (tenant_id, user_subject, created_by) VALUES (@t, @s, 'seed') RETURNING id",
            ("t", tenant), ("s", subject));
        foreach (var role in roles)
            await ExecAsync("""
                INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
                VALUES (@t, @m, @s, @r::ti.role_name, 'test seed', 'seed', 'seed')
                """, ("t", tenant), ("m", membership), ("s", subject), ("r", role));
        return subject;
    }

    private static object Observe(string bin, string product, string quantity, string? lot = null) =>
        new { binId = bin, productId = product, lotNumber = lot, observedQuantity = quantity };

    private static Task<HttpResponseMessage> Sign(HttpClient signer, string approval, string reauthToken) =>
        signer.PostJsonAsync($"/api/v1/approvals/{approval}/sign", new { reauthToken, meaning = "Approval", reason = "Recount confirms it" }, "\"1\"");

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

    private async Task<string> BucketAsync(World w, string product, string bin, decimal onHand, decimal reserved = 0, string? lot = null)
    {
        object lotId = DBNull.Value;
        if (lot is not null)
            lotId = await ScalarAsync<Guid>("""
                INSERT INTO ti.lot (tenant_id, product_id, handling_profile, lot_number, expiry_date, created_by)
                SELECT @t, id, handling_profile, @n, current_date + 90, 'test' FROM ti.product WHERE id = @p RETURNING id
                """, ("t", w.Tenant), ("p", Guid.Parse(product)), ("n", lot));
        var bucket = await ScalarAsync<Guid>("""
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, quality_status, received_at, on_hand_qty, reserved_qty, created_by)
            VALUES (@t, @w, @b, @p, @l, 'Available', now() - interval '5 days', @qty, @r, 'test') RETURNING id
            """, ("t", w.Tenant), ("w", Guid.Parse(w.Warehouse)), ("b", Guid.Parse(bin)), ("p", Guid.Parse(product)), ("l", lotId), ("qty", onHand), ("r", reserved));
        await ExecAsync("""
            INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, lot_id, destination_bucket_id, base_qty, document_type, document_id,
                                           actor_subject, occurred_at)
            VALUES (@t, 'Receipt', 'test-receipt:' || gen_random_uuid(), @p, @l, @b, @q, 'Receipt', gen_random_uuid(), 'test', now())
            """, ("t", w.Tenant), ("p", Guid.Parse(product)), ("l", lotId), ("b", bucket), ("q", onHand));
        return bucket.ToString();
    }

    private async Task<string> QtyAsync(string bucket) => await ScalarAsync<string>(
        "SELECT trim_scale(on_hand_qty)::text || '|' || trim_scale(reserved_qty)::text FROM ti.stock_bucket WHERE id = @b", ("b", Guid.Parse(bucket)));

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

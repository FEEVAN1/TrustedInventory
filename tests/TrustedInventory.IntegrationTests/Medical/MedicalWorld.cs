using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Medical;

/// <summary>
/// A tenant set up for medical tests (M5): an operator holding WarehouseManager, Picker, Dispatcher and QaOfficer, an independent QA signer,
/// one warehouse (time zone chosen by the test) with ambient and 2-8 °C zones, a lot-tracked medical product on a cold-chain profile, a
/// general product and a customer. Stock is written as the superuser with a balancing Receipt movement, like the M3/M4 tests.
/// </summary>
public sealed class MedicalWorld
{
    private readonly ApiFixture _fx;

    private MedicalWorld(ApiFixture fx) => _fx = fx;

    public Guid Tenant { get; private set; }
    public HttpClient Operator { get; private set; } = null!;
    public string OperatorSubject { get; private set; } = "";
    public HttpClient Signer { get; private set; } = null!;
    public string SignerSubject { get; private set; } = "";
    public string Warehouse { get; private set; } = "";
    public string AmbientZone { get; private set; } = "";
    public string ColdZone { get; private set; } = "";
    public string A01 { get; private set; } = "";
    public string C01 { get; private set; } = "";
    public string C02 { get; private set; } = "";
    public string Staging { get; private set; } = "";
    public string Medical { get; private set; } = "";
    public string General { get; private set; } = "";
    public string Customer { get; private set; } = "";

    public static async Task<MedicalWorld> CreateAsync(ApiFixture fx, string label, string timeZone = "UTC")
    {
        var w = new MedicalWorld(fx);
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label);
        w.Tenant = tenant.TenantId;
        w.OperatorSubject = tenant.Admin;
        w.Operator = fx.Client(tenant.Admin, tenant.TenantId);
        foreach (var role in new[] { "WarehouseManager", "Picker", "Dispatcher" })
            await TenantProvisioning.GrantSelfAsync(fx, tenant, role);
        // QA authority cannot be self-granted (SOD-3); seed it as a second administrator would.
        await w.ExecAsync("""
            INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
            SELECT tenant_id, id, user_subject, 'QaOfficer', 'test seed', 'seed', 'seed' FROM ti.membership WHERE tenant_id = @t AND user_subject = @s
            """, ("t", w.Tenant), ("s", w.OperatorSubject));
        async Task<string> Create(string path, object body) => (string)(await (await w.Operator.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!;
        w.Warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone });
        w.AmbientZone = await Create("/api/v1/zones", new { warehouseId = w.Warehouse, code = "AMB", name = "Ambient" });
        w.ColdZone = await Create("/api/v1/zones", new { warehouseId = w.Warehouse, code = "COLD", name = "Cold", tempMinC = 2, tempMaxC = 8 });
        w.A01 = await Create("/api/v1/bins", new { zoneId = w.AmbientZone, code = "A-01", kind = "Storage" });
        w.C01 = await Create("/api/v1/bins", new { zoneId = w.ColdZone, code = "C-01", kind = "Storage" });
        w.C02 = await Create("/api/v1/bins", new { zoneId = w.ColdZone, code = "C-02", kind = "Storage" });
        w.Staging = await Create("/api/v1/bins", new { zoneId = w.ColdZone, code = "STG", kind = "Staging" });
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var profile = await Create("/api/v1/storage-profiles", new
        {
            name = "Cold chain", minC = 2, maxC = 8, monitoringRequired = true, excursionPolicy = new { immediateHold = true, notes = "QA reviewed" }, reason = "test",
        });
        w.Medical = await Create("/api/v1/products", new
        {
            sku = "MED", name = "Synthetic vaccine", handlingProfile = "Medical", trackingMode = "Lot", baseUomId = unit, basePrecision = 0,
            storageProfileId = profile, rotationRule = "FEFO",
        });
        w.General = await Create("/api/v1/products", new
        {
            sku = "GEN", name = "Synthetic carton", handlingProfile = "General", trackingMode = "Quantity", baseUomId = unit, basePrecision = 0, rotationRule = "FIFO",
        });
        w.Customer = await Create("/api/v1/partners", new { code = "CUS", name = "Synthetic clinic", type = "Customer" });
        w.SignerSubject = $"user-qa-signer-{Guid.NewGuid():N}";
        var membership = await w.ScalarAsync<Guid>("INSERT INTO ti.membership (tenant_id, user_subject, created_by) VALUES (@t, @s, 'seed') RETURNING id",
            ("t", w.Tenant), ("s", w.SignerSubject));
        await w.ExecAsync("""
            INSERT INTO ti.role_grant (tenant_id, membership_id, grantee_subject, role, reason, granted_by, created_by)
            VALUES (@t, @m, @s, 'QaOfficer', 'test seed', 'seed', 'seed')
            """, ("t", w.Tenant), ("m", membership), ("s", w.SignerSubject));
        w.Signer = fx.Client(w.SignerSubject, w.Tenant);
        return w;
    }

    /// <summary>A lot of <paramref name="product"/> expiring on <paramref name="expiry"/>.</summary>
    public async Task<Guid> LotAsync(string product, string number, DateOnly expiry) => await ScalarAsync<Guid>("""
        INSERT INTO ti.lot (tenant_id, product_id, handling_profile, lot_number, expiry_date, created_by)
        SELECT @t, id, handling_profile, @n, @e, 'test' FROM ti.product WHERE id = @p RETURNING id
        """, ("t", Tenant), ("p", Guid.Parse(product)), ("n", number), ("e", expiry));

    /// <summary>A bucket of the given quality with a balancing Receipt movement recorded by <paramref name="receivedBy"/>.</summary>
    public async Task<Guid> BucketAsync(string product, string bin, decimal onHand, string quality = "Available", Guid? lot = null,
        string receivedBy = "receiver-test")
    {
        var bucket = await ScalarAsync<Guid>("""
            INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, lot_id, quality_status, received_at, on_hand_qty, created_by)
            VALUES (@t, @w, @b, @p, @l, @q::ti.quality_status, now() - interval '5 days', @qty, @by) RETURNING id
            """, ("t", Tenant), ("w", Guid.Parse(Warehouse)), ("b", Guid.Parse(bin)), ("p", Guid.Parse(product)), ("l", (object?)lot ?? DBNull.Value),
            ("q", quality), ("qty", onHand), ("by", receivedBy));
        await ExecAsync("""
            INSERT INTO ti.stock_movement (tenant_id, movement_type, operation_key, product_id, lot_id, destination_bucket_id, base_qty,
                                           document_type, document_id, actor_subject, occurred_at)
            VALUES (@t, 'Receipt', 'test-receipt:' || gen_random_uuid(), @p, @l, @b, @q, 'Receipt', gen_random_uuid(), @by, now())
            """, ("t", Tenant), ("p", Guid.Parse(product)), ("l", (object?)lot ?? DBNull.Value), ("b", bucket), ("q", onHand), ("by", receivedBy));
        return bucket;
    }

    /// <summary>"onHand|reserved|quality" of a bucket.</summary>
    public Task<string> StateAsync(Guid bucket) => ScalarAsync<string>(
        "SELECT trim_scale(on_hand_qty)::text || '|' || trim_scale(reserved_qty)::text || '|' || quality_status::text FROM ti.stock_bucket WHERE id = @b",
        ("b", bucket));

    /// <summary>The bucket of the same bin, product and lot in another quality.</summary>
    public Task<Guid> SiblingAsync(Guid bucket, string quality) => ScalarAsync<Guid>("""
        SELECT s.id FROM ti.stock_bucket b JOIN ti.stock_bucket s ON s.tenant_id = b.tenant_id AND s.bin_id = b.bin_id AND s.product_id = b.product_id
          AND s.lot_id IS NOT DISTINCT FROM b.lot_id AND s.serial_id IS NOT DISTINCT FROM b.serial_id
        WHERE b.id = @b AND s.quality_status = @q::ti.quality_status
        """, ("b", bucket), ("q", quality));

    public Task<int> BucketVersionAsync(Guid bucket) => ScalarAsync<int>("SELECT version FROM ti.stock_bucket WHERE id = @b", ("b", bucket));

    /// <summary>The operator requests and the independent QA officer signs an approval of a bucket at its current version.</summary>
    public async Task<string> ApproveBucketAsync(string subjectType, Guid bucket, string action = "Release")
    {
        var approval = (string)(await (await Operator.PostJsonAsync("/api/v1/approvals", new
        {
            subjectType, subjectId = bucket, subjectVersion = await BucketVersionAsync(bucket), action, reason = "QA review complete",
        })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await Signer.PostJsonAsync($"/api/v1/approvals/{approval}/sign",
            new { reauthToken = TestTokens.Issue(SignerSubject), meaning = "QualityRelease", reason = "Evidence checked" }, "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        return approval;
    }

    /// <summary>A confirmed order for the customer, dispatching tomorrow.</summary>
    public async Task<string> OrderAsync(string product, string quantity, int? minShelfLifeDays = null)
    {
        var created = await (await Operator.PostJsonAsync("/api/v1/orders", new
        {
            customerId = Customer, warehouseId = Warehouse, reference = $"SO-{Guid.NewGuid():N}"[..20],
            plannedDispatchDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            lines = new[] { new { productId = product, quantity, minRemainingShelfLifeDays = minShelfLifeDays } },
        })).ExpectAsync(HttpStatusCode.Created);
        await (await Operator.PostEmptyAsync($"/api/v1/orders/{created["id"]}/confirm", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        return (string)created["id"]!;
    }

    public Task<HttpResponseMessage> AllocateAsync(string order) => PostAsync($"/api/v1/orders/{order}/allocate", Guid.NewGuid(), "\"2\"", new { allowPartial = false });

    /// <summary>Picks the order's single open task in full into staging.</summary>
    public async Task<HttpResponseMessage> PickAllAsync(string order, string bin, string? lotNumber)
    {
        var task = (await (await Operator.GetAsync($"/api/v1/picks?orderId={order}")).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray().Single()!;
        return await PostAsync($"/api/v1/picks/{task["id"]}/confirm", Guid.NewGuid(), $"\"{task["version"]}\"", new
        {
            quantity = task["remainingQuantity"]!.ToString(), scannedBinId = bin, scannedProductId = task["productId"]!.ToString(),
            scannedLotNumber = lotNumber, stagingBinId = Staging,
        });
    }

    public async Task<HttpResponseMessage> DispatchAsync(string order)
    {
        var picks = (await (await Operator.GetAsync($"/api/v1/picks?orderId={order}")).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray()
            .Select(p => (string)p!["id"]!).ToArray();
        var version = (await (await Operator.GetAsync($"/api/v1/orders/{order}")).ExpectAsync(HttpStatusCode.OK))["version"]!.ToString();
        return await PostAsync($"/api/v1/orders/{order}/dispatch", Guid.NewGuid(), $"\"{version}\"",
            new { pickIds = picks, allowPartial = false, dispatchReference = $"DN-{Guid.NewGuid():N}"[..12] });
    }

    public Task<HttpResponseMessage> PostAsync(string url, Guid key, string? ifMatch, object body, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", key.ToString());
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return (client ?? Operator).SendAsync(request);
    }

    public async Task<JsonObject> GetAsync(string url) => await (await Operator.GetAsync(url)).ExpectAsync(HttpStatusCode.OK);

    public Task<int> ExecAsync(string sql, params (string Name, object Value)[] args) => TenantProvisioning.ExecAsSuperuserAsync(_fx, sql, args);

    public async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args) =>
        (T)(await TenantProvisioning.ScalarAsSuperuserAsync(_fx, sql, args))!;
}

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Outbound;

/// <summary>Outbound order workflow (OUT-001, OUT-006 cancellation, DEC-052) through the API on real PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class OutboundOrderTests(ApiFixture fx)
{
    private const string Orders = "/api/v1/orders";

    [Fact]
    [Trait("Requirement", "OUT-001")]
    [Trait("Requirement", "AUD-001")]
    public async Task A_draft_is_edited_then_confirmed_and_its_lines_freeze()
    {
        var w = await WorldAsync("so-flow");
        var created = await w.Client.PostJsonAsync(Orders, Order(w, "SO-1", Tomorrow(w), Line(w.General, "10"), Line(w.Fractional, "2.5", 30)));
        Assert.Equal("\"1\"", created.ETag());
        var draft = await created.ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("Draft", (string?)draft["status"]);
        var lines = draft["lines"]!.AsArray();
        Assert.Equal(["10", "2.5"], lines.Select(l => (string)l!["requestedQuantity"]!));
        Assert.Equal([0, 30], lines.Select(l => (int)l!["minRemainingShelfLifeDays"]!));
        Assert.All(lines, l => Assert.Equal(["0", "0", "0"], new[] { (string)l!["reservedQuantity"]!, (string)l["pickedQuantity"]!, (string)l["dispatchedQuantity"]! }));
        var id = (string)draft["id"]!;

        var edited = await (await w.Client.PatchJsonAsync($"{Orders}/{id}", new { reference = "SO-1A", lines = new[] { Line(w.General, "4") } }, "\"1\""))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(2, (int)edited["version"]!);
        Assert.Equal("4", (string?)Assert.Single(edited["lines"]!.AsArray())!["requestedQuantity"]);

        var confirmed = await (await w.Client.PostEmptyAsync($"{Orders}/{id}/confirm", "\"2\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Confirmed", (string?)confirmed["status"]);
        await (await w.Client.PatchJsonAsync($"{Orders}/{id}", new { reference = "SO-1B" }, "\"3\"")).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await w.Client.PostEmptyAsync($"{Orders}/{id}/confirm", "\"3\"")).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await w.Client.PostEmptyAsync($"{Orders}/{id}/confirm", "\"1\"")).ExpectProblemAsync(HttpStatusCode.PreconditionFailed, "VERSION_CONFLICT");

        Assert.Equal(["OutboundOrderCreated", "OutboundOrderUpdated", "OutboundOrderConfirmed"], await ListAsync(
            "SELECT event_type FROM ti.audit_event WHERE tenant_id = @t AND object_id = @o ORDER BY sequence", ("t", w.Tenant), ("o", Guid.Parse(id))));
    }

    [Fact]
    [Trait("Requirement", "OUT-001")]
    [Trait("Requirement", "STK-PRECISION")]
    public async Task Orders_need_active_products_valid_quantities_a_customer_and_a_dispatch_date_not_in_the_past()
    {
        var w = await WorldAsync("so-valid");
        async Task<string?> Rejected(object body) =>
            (string?)Assert.Single((await (await w.Client.PostJsonAsync(Orders, body)).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"))
                ["errors"]!.AsArray())!["field"];

        Assert.Equal("lines[0].productId", await Rejected(Order(w, "SO-V1", Tomorrow(w), Line(Guid.NewGuid().ToString(), "1"))));
        Assert.Equal("lines[0].productId", await Rejected(Order(w, "SO-V2", Tomorrow(w), Line(w.Retired, "1"))));
        Assert.Equal("lines[0].quantity", await Rejected(Order(w, "SO-V3", Tomorrow(w), Line(w.General, "1.5"))));     // precision 0
        Assert.Equal("lines[0].quantity", await Rejected(Order(w, "SO-V4", Tomorrow(w), Line(w.Fractional, "1.125")))); // precision 2
        Assert.Equal("lines[0].quantity", await Rejected(Order(w, "SO-V5", Tomorrow(w), Line(w.General, "0"))));
        Assert.Equal("lines[0].minRemainingShelfLifeDays", await Rejected(Order(w, "SO-V6", Tomorrow(w), Line(w.General, "1", 3651))));
        Assert.Equal("plannedDispatchDate", await Rejected(Order(w, "SO-V7", Today(w).AddDays(-1), Line(w.General, "1"))));
        Assert.Equal("customerId", await Rejected(Order(w, "SO-V8", Tomorrow(w), w.Supplier, Line(w.General, "1"))));
        Assert.Equal("reference", await Rejected(Order(w, "bad ref!", Tomorrow(w), Line(w.General, "1"))));
        await (await w.Client.PostJsonAsync(Orders, Order(w, "SO-V9", Today(w), Line(w.General, "1")))).ExpectAsync(HttpStatusCode.Created); // today is fine
        Assert.Equal("reference", await Rejected(Order(w, "SO-V9", Tomorrow(w), Line(w.General, "1"))));

        // Confirmation rechecks against current data: a product retired since, or a date that has passed.
        var stale = (string)(await (await w.Client.PostJsonAsync(Orders, Order(w, "SO-V10", Tomorrow(w), Line(w.General, "1")))).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await ExecAsync("UPDATE ti.outbound_order SET planned_dispatch_date = current_date - 30 WHERE id = @o", ("o", Guid.Parse(stale)));
        await (await w.Client.PostEmptyAsync($"{Orders}/{stale}/confirm", "\"1\"")).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
    }

    [Fact]
    [Trait("Requirement", "OUT-006")]
    [Trait("Requirement", "STK-007")]
    [Trait("Requirement", "STK-002")]
    public async Task Cancelling_releases_active_reservations_once_and_refuses_while_stock_is_picked()
    {
        var w = await WorldAsync("so-cancel");
        // A confirmed order holding a 4-unit reservation on a 10-unit bucket (allocation arrives in slice 3).
        var (order, line) = await ConfirmedOrderAsync(w, "SO-C1", "10");
        var bucket = await BucketAsync(w, 10m);
        var reservation = await ReserveAsync(w, line, bucket, 4m);

        // Cancel needs a key, a version and a reason.
        await (await PostAsync(w, $"{Orders}/{order}/cancel", null, "\"2\"", new { reason = "x" })).ExpectProblemAsync(HttpStatusCode.BadRequest, "IDEMPOTENCY_KEY_REQUIRED");
        await (await PostAsync(w, $"{Orders}/{order}/cancel", Guid.NewGuid(), null, new { reason = "x" })).ExpectProblemAsync((HttpStatusCode)428, "PRECONDITION_REQUIRED");
        await (await PostAsync(w, $"{Orders}/{order}/cancel", Guid.NewGuid(), "\"2\"", new { reason = " " })).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");

        var key = Guid.NewGuid();
        var cancelled = await (await PostAsync(w, $"{Orders}/{order}/cancel", key, "\"2\"", new { reason = "Customer withdrew" })).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(["Cancelled", "Customer withdrew", "0"],
            new[] { (string)cancelled["status"]!, (string)cancelled["cancelledReason"]!, (string)cancelled["lines"]![0]!["reservedQuantity"]! });
        Assert.Equal("10|0", await BucketQtyAsync(bucket));
        Assert.Equal("Released", await ScalarAsync<string>("SELECT state::text FROM ti.reservation WHERE id = @r", ("r", reservation)));
        var replay = await (await PostAsync(w, $"{Orders}/{order}/cancel", key, "\"2\"", new { reason = "Customer withdrew" })).ExpectAsync(HttpStatusCode.OK);
        Assert.True(JsonNode.DeepEquals(cancelled, replay));
        Assert.Equal("10|0", await BucketQtyAsync(bucket)); // released once

        // With stock already picked, cancellation is refused and nothing is released.
        var (picked, pickedLine) = await ConfirmedOrderAsync(w, "SO-C2", "5");
        var other = await ReserveAsync(w, pickedLine, bucket, 3m);
        await ExecAsync("UPDATE ti.outbound_order_line SET picked_base_qty = 1 WHERE id = @l", ("l", Guid.Parse(pickedLine)));
        await (await PostAsync(w, $"{Orders}/{picked}/cancel", Guid.NewGuid(), "\"2\"", new { reason = "Too late" })).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        Assert.Equal("Active", await ScalarAsync<string>("SELECT state::text FROM ti.reservation WHERE id = @r", ("r", other)));
        Assert.Equal("10|3", await BucketQtyAsync(bucket));
    }

    [Fact]
    [Trait("Requirement", "OUT-006")]
    [Trait("Requirement", "LFC-001")]
    [Trait("Requirement", "OPS-001")]
    public async Task Cancellation_is_a_safety_action_that_works_while_restricted_and_on_blocked_stock()
    {
        var w = await WorldAsync("so-safety");
        var (order, line) = await ConfirmedOrderAsync(w, "SO-S1", "10");
        var bucket = await BucketAsync(w, 10m);
        await ReserveAsync(w, line, bucket, 6m);

        // Block the bucket (OPS-001) and let the trial end (billing-restricted).
        var alert = await ScalarAsync<Guid>("""
            INSERT INTO ti.alert (tenant_id, type, severity, dedupe_key, details, created_by)
            VALUES (@t, 'ReconciliationMismatch', 'High', 'ledger-reconciliation', '{}', 'test') RETURNING id
            """, ("t", w.Tenant));
        await ExecAsync("""
            INSERT INTO ti.stock_bucket_block (tenant_id, bucket_id, alert_id, expected_qty, stored_qty, created_by)
            VALUES (@t, @b, @a, 9, 10, 'test')
            """, ("t", w.Tenant), ("b", Guid.Parse(bucket)), ("a", alert));
        await ExecAsync("""
            UPDATE cp.subscription SET state = 'Trialing', price_version_id = NULL, period_start = NULL, period_end = NULL,
                   trial_start = now() - interval '15 days', trial_end = now() - interval '1 day'
            WHERE tenant_id = @t
            """, ("t", w.Tenant));

        await (await w.Client.PostJsonAsync(Orders, Order(w, "SO-S2", Tomorrow(w), Line(w.General, "1")))).ExpectProblemAsync(HttpStatusCode.Forbidden, "SUBSCRIPTION_RESTRICTED");
        await (await PostAsync(w, $"{Orders}/{order}/cancel", Guid.NewGuid(), "\"2\"", new { reason = "Account on hold" })).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("10|0", await BucketQtyAsync(bucket)); // the commitment is released; on hand untouched
    }

    [Fact]
    [Trait("Requirement", "OUT-001")]
    [Trait("Requirement", "IAM-001")]
    public async Task Order_writes_and_reads_are_warehouse_scoped()
    {
        var w = await WorldAsync("so-scope");
        var inW2 = (string)(await (await w.Client.PostJsonAsync(Orders, Order(w, "SO-W2", Tomorrow(w), w.Customer, w.Warehouse2, Line(w.General, "1"))))
            .ExpectAsync(HttpStatusCode.Created))["id"]!;
        var scoped = await MemberAsync(w, "WarehouseManager", w.Warehouse);
        await (await scoped.PostJsonAsync(Orders, Order(w, "SO-X", Tomorrow(w), w.Customer, w.Warehouse2, Line(w.General, "1"))))
            .ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        var mine = (string)(await (await scoped.PostJsonAsync(Orders, Order(w, "SO-W1", Tomorrow(w), Line(w.General, "1")))).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await scoped.GetAsync($"{Orders}/{inW2}")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await scoped.PostEmptyAsync($"{Orders}/{inW2}/confirm", "\"1\"")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        Assert.Equal([mine], (await (await scoped.GetAsync(Orders)).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray().Select(o => (string)o!["id"]!));
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, HttpClient Client, string Warehouse, string Warehouse2, string Storage, string General, string Fractional,
        string Retired, string Customer, string Supplier, string TimeZone);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "WarehouseManager");
        async Task<string> Create(string path, object body) => (string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!;
        const string timeZone = "Pacific/Kiritimati"; // UTC+14: the warehouse date is often a day ahead of UTC
        var warehouse = await Create("/api/v1/warehouses", new { code = "W1", name = "Main", timeZone });
        var warehouse2 = await Create("/api/v1/warehouses", new { code = "W2", name = "Other", timeZone });
        var zone = await Create("/api/v1/zones", new { warehouseId = warehouse, code = "Z1", name = "Ambient" });
        var storage = await Create("/api/v1/bins", new { zoneId = zone, code = "A-01", kind = "Storage" });
        var unit = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        object Product(string sku, int precision) => new { sku, name = sku, handlingProfile = "General", trackingMode = "Quantity", baseUomId = unit, basePrecision = precision };
        var general = await Create("/api/v1/products", Product("GEN", 0));
        var fractional = await Create("/api/v1/products", Product("FRAC", 2));
        var retired = await Create("/api/v1/products", Product("OLD", 0));
        await (await admin.PostEmptyAsync($"/api/v1/products/{retired}/retire", "\"1\"")).ExpectAsync(HttpStatusCode.NoContent);
        var customer = await Create("/api/v1/partners", new { code = "CUS", name = "Customer", type = "Customer" });
        var supplier = await Create("/api/v1/partners", new { code = "SUP", name = "Supplier", type = "Supplier" });
        return new World(tenant.TenantId, admin, warehouse, warehouse2, storage, general, fractional, retired, customer, supplier, timeZone);
    }

    private static DateOnly Today(World w) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(w.TimeZone)).DateTime);

    private static DateOnly Tomorrow(World w) => Today(w).AddDays(1);

    private static object Line(string product, string quantity, int? shelfLife = null) =>
        new { productId = product, quantity, minRemainingShelfLifeDays = shelfLife };

    private static object Order(World w, string reference, DateOnly date, params object[] lines) => Order(w, reference, date, w.Customer, w.Warehouse, lines);

    private static object Order(World w, string reference, DateOnly date, string customer, params object[] lines) => Order(w, reference, date, customer, w.Warehouse, lines);

    private static object Order(World w, string reference, DateOnly date, string customer, string warehouse, params object[] lines) =>
        new { customerId = customer, warehouseId = warehouse, reference, plannedDispatchDate = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), lines };

    private static async Task<(string Order, string Line)> ConfirmedOrderAsync(World w, string reference, string quantity)
    {
        var order = await (await w.Client.PostJsonAsync(Orders, Order(w, reference, Tomorrow(w), Line(w.General, quantity)))).ExpectAsync(HttpStatusCode.Created);
        await (await w.Client.PostEmptyAsync($"{Orders}/{order["id"]}/confirm", "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        return ((string)order["id"]!, (string)order["lines"]![0]!["id"]!);
    }

    private async Task<string> BucketAsync(World w, decimal onHand) => (await ScalarAsync<Guid>("""
        INSERT INTO ti.stock_bucket (tenant_id, warehouse_id, bin_id, product_id, quality_status, received_at, on_hand_qty, created_by)
        VALUES (@t, @w, @b, @p, 'Available', now(), @q, 'test') RETURNING id
        """, ("t", w.Tenant), ("w", Guid.Parse(w.Warehouse)), ("b", Guid.Parse(w.Storage)), ("p", Guid.Parse(w.General)), ("q", onHand))).ToString();

    /// <summary>A balanced reservation as allocation (slice 3) will write it: reservation, bucket reserved, line allocated.</summary>
    private async Task<Guid> ReserveAsync(World w, string line, string bucket, decimal qty)
    {
        var id = await ScalarAsync<Guid>("""
            INSERT INTO ti.reservation (tenant_id, order_line_id, product_id, bucket_id, qty, expires_at, created_by)
            VALUES (@t, @l, @p, @b, @q, now() + interval '24 hours', 'test') RETURNING id
            """, ("t", w.Tenant), ("l", Guid.Parse(line)), ("p", Guid.Parse(w.General)), ("b", Guid.Parse(bucket)), ("q", qty));
        await ExecAsync("UPDATE ti.stock_bucket SET reserved_qty = reserved_qty + @q WHERE id = @b", ("q", qty), ("b", Guid.Parse(bucket)));
        await ExecAsync("UPDATE ti.outbound_order_line SET allocated_base_qty = allocated_base_qty + @q WHERE id = @l", ("q", qty), ("l", Guid.Parse(line)));
        return id;
    }

    private async Task<string> BucketQtyAsync(string bucket) => await ScalarAsync<string>(
        "SELECT trim_scale(on_hand_qty)::text || '|' || trim_scale(reserved_qty)::text FROM ti.stock_bucket WHERE id = @b", ("b", Guid.Parse(bucket)));

    private async Task<HttpClient> MemberAsync(World w, string role, string warehouse)
    {
        var subject = $"user-scoped-{Guid.NewGuid():N}";
        var membership = (string)(await (await w.Client.PostJsonAsync("/api/v1/memberships", new { userSubject = subject })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await w.Client.PostJsonAsync($"/api/v1/memberships/{membership}/role-grants", new { role, warehouseId = warehouse, reason = "order test" }))
            .ExpectAsync(HttpStatusCode.Created);
        return fx.Client(subject, w.Tenant);
    }

    private static Task<HttpResponseMessage> PostAsync(World w, string url, Guid? key, string? ifMatch, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), System.Text.Encoding.UTF8, "application/json"),
        };
        if (key is { } k)
            request.Headers.Add("Idempotency-Key", k.ToString());
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return w.Client.SendAsync(request);
    }

    private Task<int> ExecAsync(string sql, params (string Name, object Value)[] args) => TenantProvisioning.ExecAsSuperuserAsync(fx, sql, args);

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] args) =>
        (T)(await TenantProvisioning.ScalarAsSuperuserAsync(fx, sql, args))!;

    private async Task<List<string>> ListAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new Npgsql.NpgsqlConnection(fx.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return rows;
    }
}

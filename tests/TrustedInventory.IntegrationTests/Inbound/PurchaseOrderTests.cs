using System.Net;
using System.Text.Json.Nodes;
using TrustedInventory.IntegrationTests.Organization;
using TrustedInventory.IntegrationTests.Support;

namespace TrustedInventory.IntegrationTests.Inbound;

/// <summary>Purchase order workflow (INB-001, DEC-047) through the API on real PostgreSQL.</summary>
[Collection(ApiTestGroup.Name)]
public sealed class PurchaseOrderTests(ApiFixture fx)
{
    private const string Orders = "/api/v1/purchase-orders";

    [Fact]
    [Trait("Requirement", "INB-001")]
    [Trait("Requirement", "API-002")]
    [Trait("Requirement", "AUD-001")]
    public async Task A_draft_is_edited_then_approved_and_its_lines_freeze()
    {
        var w = await WorldAsync("po-flow");

        var created = await w.Manager.PostJsonAsync(Orders, Order(w, w.Warehouse1, "PO-1", Line(w.General, "2", w.Box), Line(w.General, "5", w.Each)));
        Assert.Equal("\"1\"", created.ETag());
        var draft = await created.ExpectAsync(HttpStatusCode.Created);
        Assert.Equal("Draft", (string?)draft["status"]);
        var lines = draft["lines"]!.AsArray();
        Assert.Equal([1, 2], lines.Select(l => (int)l!["lineNumber"]!));
        Assert.Equal(["12", "1"], lines.Select(l => (string)l!["factorToBase"]!));
        Assert.Equal(["24", "5"], lines.Select(l => (string)l!["orderedBaseQuantity"]!));
        Assert.All(lines, l => Assert.Equal("0", (string?)l!["receivedBaseQuantity"]));
        Assert.Equal(["24", "5"], lines.Select(l => (string)l!["outstandingBaseQuantity"]!));
        var id = (string)draft["id"]!;

        // Editing a draft replaces all lines and renumbers them.
        var edited = await (await w.Manager.PatchJsonAsync($"{Orders}/{id}", new { reference = "PO-1A", lines = new[] { Line(w.General, "10", w.Each) } }, "\"1\""))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(2, (int)edited["version"]!);
        Assert.Equal("PO-1A", (string?)edited["reference"]);
        Assert.Equal("10", (string?)Assert.Single(edited["lines"]!.AsArray())!["orderedBaseQuantity"]);

        var approved = await (await w.Manager.PostEmptyAsync($"{Orders}/{id}/approve", "\"2\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Approved", (string?)approved["status"]);
        Assert.Equal(w.ManagerSubject, (string?)approved["approvedBy"]);
        Assert.NotNull(approved["approvedAt"]);

        // Approved orders are frozen, in the service and in the database (0007 trigger).
        await (await w.Manager.PatchJsonAsync($"{Orders}/{id}", new { reference = "PO-1B" }, "\"3\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await w.Manager.PostEmptyAsync($"{Orders}/{id}/approve", "\"3\"")).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await w.Manager.PostEmptyAsync($"{Orders}/{id}/approve", "\"1\"")).ExpectProblemAsync(HttpStatusCode.PreconditionFailed, "VERSION_CONFLICT");

        var fetched = await (await w.Manager.GetAsync($"{Orders}/{id}")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal(3, (int)fetched["version"]!);
        var events = await ListAsync("SELECT event_type FROM ti.audit_event WHERE tenant_id = @t AND object_id = @o ORDER BY sequence",
            ("t", w.Tenant), ("o", Guid.Parse(id)));
        Assert.Equal(["PurchaseOrderCreated", "PurchaseOrderUpdated", "PurchaseOrderApproved"], events);
    }

    [Fact]
    [Trait("Requirement", "INB-001")]
    [Trait("Requirement", "STK-PRECISION")]
    public async Task Lines_must_resolve_to_an_ordered_unit_and_respect_product_precision()
    {
        var w = await WorldAsync("po-lines");
        async Task<string?> Rejected(object body) =>
            (string?)Assert.Single((await (await w.Manager.PostJsonAsync(Orders, body)).ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED"))
                ["errors"]!.AsArray())!["field"];

        Assert.Equal("lines[0].productId", await Rejected(Order(w, w.Warehouse1, "PO-L1", Line(Guid.NewGuid(), "1", w.Each))));
        Assert.Equal("lines[0].uomId", await Rejected(Order(w, w.Warehouse1, "PO-L2", Line(w.Serial, "1", w.Box))));           // no conversion
        Assert.Equal("lines[1].quantity", await Rejected(Order(w, w.Warehouse1, "PO-L3", Line(w.General, "1", w.Each), Line(w.General, "1.5", w.Each)))); // precision 0
        Assert.Equal("lines[0].quantity", await Rejected(Order(w, w.Warehouse1, "PO-L4", Line(w.Fractional, "1.234", w.Each)))); // precision 2
        Assert.Equal("lines[0].quantity", await Rejected(Order(w, w.Warehouse1, "PO-L5", Line(w.General, "0", w.Each))));
        Assert.Equal("lines[0].quantity", await Rejected(Order(w, w.Warehouse1, "PO-L6", Line(w.General, "1.1234567", w.Each))));
        Assert.Equal("supplierId", await Rejected(Order(w, w.Warehouse1, "PO-L7", w.Customer, Line(w.General, "1", w.Each))));
        Assert.Equal("reference", await Rejected(Order(w, w.Warehouse1, "bad ref!", Line(w.General, "1", w.Each))));

        // A fractional quantity in a larger unit is fine when the base quantity is whole: half a box of 12 is 6.
        var ok = await (await w.Manager.PostJsonAsync(Orders, Order(w, w.Warehouse1, "PO-L8", Line(w.General, "0.5", w.Box), Line(w.Fractional, "1.25", w.Each))))
            .ExpectAsync(HttpStatusCode.Created);
        Assert.Equal(["6", "1.25"], ok["lines"]!.AsArray().Select(l => (string)l!["orderedBaseQuantity"]!));
        Assert.Equal("reference", await Rejected(Order(w, w.Warehouse1, "PO-L8", Line(w.General, "1", w.Each)))); // unique per tenant
    }

    [Fact]
    [Trait("Requirement", "INB-001")]
    [Trait("Requirement", "AUD-001")]
    public async Task Orders_close_only_by_cancelling_before_receipt_or_short_closing_with_a_reason()
    {
        var w = await WorldAsync("po-close");
        async Task<(string Id, string ETag)> Approved(string reference)
        {
            var o = await (await w.Manager.PostJsonAsync(Orders, Order(w, w.Warehouse1, reference, Line(w.General, "10", w.Each)))).ExpectAsync(HttpStatusCode.Created);
            var a = await w.Manager.PostEmptyAsync($"{Orders}/{o["id"]}/approve", "\"1\"");
            await a.ExpectAsync(HttpStatusCode.OK);
            return ((string)o["id"]!, a.ETag());
        }

        // A draft cancels with a reason, which the audit event records.
        var draft = (string)(await (await w.Manager.PostJsonAsync(Orders, Order(w, w.Warehouse1, "PO-C1", Line(w.General, "1", w.Each)))).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await w.Manager.PostJsonAsync($"{Orders}/{draft}/short-close", new { reason = "not yet approved" }, "\"1\""))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        var cancelled = await (await w.Manager.PostJsonAsync($"{Orders}/{draft}/cancel", new { reason = "Supplier withdrew" }, "\"1\"")).ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("Cancelled", (string?)cancelled["status"]);
        Assert.Equal("Supplier withdrew", await ScalarAsync<string>(
            "SELECT reason FROM ti.audit_event WHERE tenant_id = @t AND object_id = @o AND event_type = 'PurchaseOrderCancelled'", ("t", w.Tenant), ("o", Guid.Parse(draft))));
        await (await w.Manager.PostEmptyAsync($"{Orders}/{draft}/approve", "\"2\"")).ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE"); // final

        // Approved with nothing received may still be cancelled.
        var (unreceived, etag) = await Approved("PO-C2");
        await (await w.Manager.PostJsonAsync($"{Orders}/{unreceived}/cancel", new { reason = "Duplicate order" }, etag)).ExpectAsync(HttpStatusCode.OK);

        // Once something is received, only a reasoned short-close ends it.
        var (partial, partialTag) = await Approved("PO-C3");
        await ExecAsync("UPDATE ti.purchase_order_line SET received_base_qty = 4 WHERE purchase_order_id = @o", ("o", Guid.Parse(partial)));
        await ExecAsync("UPDATE ti.purchase_order SET status = 'PartReceived' WHERE id = @o", ("o", Guid.Parse(partial))); // stands in for slice 3
        await (await w.Manager.PostJsonAsync($"{Orders}/{partial}/cancel", new { reason = "Too late" }, partialTag))
            .ExpectProblemAsync(HttpStatusCode.Conflict, "INVALID_STATE");
        await (await w.Manager.PostJsonAsync($"{Orders}/{partial}/short-close", new { reason = " " }, partialTag))
            .ExpectProblemAsync(HttpStatusCode.UnprocessableEntity, "VALIDATION_FAILED");
        var closed = await (await w.Manager.PostJsonAsync($"{Orders}/{partial}/short-close", new { reason = "Supplier discontinued the rest" }, partialTag))
            .ExpectAsync(HttpStatusCode.OK);
        Assert.Equal("ShortClosed", (string?)closed["status"]);
        Assert.Equal("Supplier discontinued the rest", (string?)closed["shortCloseReason"]);
        Assert.Equal("6", (string?)Assert.Single(closed["lines"]!.AsArray())!["outstandingBaseQuantity"]);
    }

    [Fact]
    [Trait("Requirement", "INB-001")]
    [Trait("Requirement", "IAM-001")]
    public async Task Warehouse_scoped_managers_work_only_in_their_warehouse_and_readers_cannot_approve()
    {
        var w = await WorldAsync("po-scope");
        var inW2 = (string)(await (await w.Manager.PostJsonAsync(Orders, Order(w, w.Warehouse2, "PO-S2", Line(w.General, "1", w.Each)))).ExpectAsync(HttpStatusCode.Created))["id"]!;

        var scoped = await MemberAsync(w, "scoped", "WarehouseManager", w.Warehouse1);
        await (await scoped.PostJsonAsync(Orders, Order(w, w.Warehouse2, "PO-S3", Line(w.General, "1", w.Each)))).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        var inW1 = (string)(await (await scoped.PostJsonAsync(Orders, Order(w, w.Warehouse1, "PO-S1", Line(w.General, "1", w.Each)))).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await scoped.GetAsync($"{Orders}/{inW2}")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await scoped.PostEmptyAsync($"{Orders}/{inW2}/approve", "\"1\"")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        var visible = (await (await scoped.GetAsync(Orders)).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray().Select(o => (string)o!["id"]!).ToList();
        Assert.Equal([inW1], visible);

        // A receiver reads orders in every warehouse but neither creates nor approves them.
        var receiver = await MemberAsync(w, "receiver", "Receiver", null);
        Assert.Equal(2, (await (await receiver.GetAsync(Orders)).ExpectAsync(HttpStatusCode.OK))["items"]!.AsArray().Count);
        await (await receiver.PostEmptyAsync($"{Orders}/{inW1}/approve", "\"1\"")).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
        await (await receiver.PostJsonAsync(Orders, Order(w, w.Warehouse1, "PO-S4", Line(w.General, "1", w.Each)))).ExpectProblemAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed record World(Guid Tenant, HttpClient Admin, HttpClient Manager, string ManagerSubject, string Warehouse1, string Warehouse2,
        string Each, string Box, string General, string Fractional, string Serial, string Supplier, string Customer);

    private async Task<World> WorldAsync(string label)
    {
        var tenant = await TenantProvisioning.ProvisionAsync(fx, label);
        var admin = fx.Client(tenant.Admin, tenant.TenantId);
        await TenantProvisioning.GrantSelfAsync(fx, tenant, "WarehouseManager"); // the administrator also manages purchasing
        async Task<string> Create(string path, object body) => (string)(await (await admin.PostJsonAsync(path, body)).ExpectAsync(HttpStatusCode.Created))["id"]!;

        var w1 = await Create("/api/v1/warehouses", new { code = "W1", name = "North", timeZone = "UTC" });
        var w2 = await Create("/api/v1/warehouses", new { code = "W2", name = "South", timeZone = "UTC" });
        var each = await Create("/api/v1/units", new { code = "EA", name = "Each" });
        var box = await Create("/api/v1/units", new { code = "BOX", name = "Box" });
        object Product(string sku, string tracking, int precision) =>
            new { sku, name = sku, handlingProfile = "General", trackingMode = tracking, baseUomId = each, basePrecision = precision };
        var general = await Create("/api/v1/products", Product("GEN", "Quantity", 0));
        var fractional = await Create("/api/v1/products", Product("FRAC", "Quantity", 2));
        var serial = await Create("/api/v1/products", Product("SER", "Serial", 0));
        await Create($"/api/v1/products/{general}/conversions", new { alternateUomId = box, factorToBase = "12" });
        var supplier = await Create("/api/v1/partners", new { code = "SUP", name = "Supplier", type = "Supplier" });
        var customer = await Create("/api/v1/partners", new { code = "CUS", name = "Customer", type = "Customer" });
        return new World(tenant.TenantId, admin, admin, tenant.Admin, w1, w2, each, box, general, fractional, serial, supplier, customer);
    }

    private async Task<HttpClient> MemberAsync(World w, string label, string role, string? warehouse)
    {
        var subject = $"user-{label}-{Guid.NewGuid():N}";
        var membership = (string)(await (await w.Admin.PostJsonAsync("/api/v1/memberships", new { userSubject = subject })).ExpectAsync(HttpStatusCode.Created))["id"]!;
        await (await w.Admin.PostJsonAsync($"/api/v1/memberships/{membership}/role-grants", new { role, warehouseId = warehouse, reason = "purchase order test" }))
            .ExpectAsync(HttpStatusCode.Created);
        return fx.Client(subject, w.Tenant);
    }

    private static object Line(object product, string quantity, string uom) => new { productId = product, quantity, uomId = uom };

    private static object Order(World w, string warehouse, string reference, params object[] lines) =>
        new { supplierId = w.Supplier, warehouseId = warehouse, reference, lines };

    private static object Order(World w, string warehouse, string reference, string supplier, params object[] lines) =>
        new { supplierId = supplier, warehouseId = warehouse, reference, lines };

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
